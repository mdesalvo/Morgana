using System.Diagnostics;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Actors;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Controllers;

/// <summary>
/// REST API for conversation lifecycle (start/end/resume/message/history), published commands and message routing to actor system.
/// Integrates with SignalR for real-time bidirectional communication; OTel turn Activity boundary.
/// Validates channel metadata, authentication, rate limits, dust budget at every request gate.
/// </summary>
[ApiController]
[Route("api/morgana")]
public class MorganaController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IChannelService channelService;
    private readonly IChannelServiceFactory channelServiceFactory;
    private readonly IChannelMetadataStore channelMetadataStore;
    private readonly IConversationPersistenceService conversationPersistenceService;
    private readonly IAuthenticationService authenticationService;
    private readonly IRateLimitService rateLimitService;
    private readonly Records.RateLimitOptions rateLimitOptions;
    private readonly IDustLimitService dustLimitService;
    private readonly Records.DustLimitingOptions dustLimitingOptions;
    private readonly ICommandRegistryService commandRegistryService;

    /// <summary>
    /// Initializes controller with actor system, authentication, rate/dust limits, channel factory and command registry.
    /// Validates channel metadata handshake and delivery mode at conversation start.
    /// </summary>
    public MorganaController(
        ActorSystem actorSystem,
        ILogger logger,
        IChannelService channelService,
        IChannelServiceFactory channelServiceFactory,
        IChannelMetadataStore channelMetadataStore,
        IConversationPersistenceService conversationPersistenceService,
        IAuthenticationService authenticationService,
        IRateLimitService rateLimitService,
        IOptions<Records.RateLimitOptions> rateLimitOptions,
        IDustLimitService dustLimitService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        ICommandRegistryService commandRegistryService)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.channelService = channelService;
        this.channelServiceFactory = channelServiceFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.conversationPersistenceService = conversationPersistenceService;
        this.authenticationService = authenticationService;
        this.rateLimitService = rateLimitService;
        this.rateLimitOptions = rateLimitOptions.Value;
        this.dustLimitService = dustLimitService;
        this.dustLimitingOptions = dustLimitingOptions.Value;
        this.commandRegistryService = commandRegistryService;
    }

    /// <summary>
    /// Starts a new conversation: settles its handshake on record, then creates the
    /// ConversationManagerActor and triggers presentation generation.
    /// </summary>
    /// <param name="request">Request containing the conversation ID to start</param>
    /// <returns>
    /// 202 Accepted once the conversation exists on record: a message sent right after is served.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/start")]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        try
        {
            (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;

            logger.LogInformation("Starting conversation {RequestConversationId}", request.ConversationId);

            // Morgana refuses to host a conversation for a channel that does not announce
            // its identity, its capability budget AND a delivery mode that matches a concrete
            // transport registered in DI. The handshake is the only place where we learn who
            // the peer is, what it can render and which transport will carry outbound messages,
            // so any missing or unknown field here is rejected explicitly rather than silently
            // defaulted. The finite set of valid deliveryMode values is owned by
            // IChannelServiceFactory; consulting it at the gate lets us fail at the earliest
            // honest point instead of surfacing the mismatch on the first outbound send.
            if (request.ChannelMetadata is null
                || request.ChannelMetadata.Coordinates is null
                || string.IsNullOrWhiteSpace(request.ChannelMetadata.Coordinates.ChannelName)
                || string.IsNullOrWhiteSpace(request.ChannelMetadata.Coordinates.DeliveryMode)
                || request.ChannelMetadata.Capabilities is null
                || !channelServiceFactory.IsRegistered(request.ChannelMetadata.Coordinates.DeliveryMode))
            {
                logger.LogWarning(
                    "Start requested for conversation {ConversationId} with incomplete or unknown channel metadata; returning 400", request.ConversationId);
                return BadRequest(new
                {
                    error = "Channel metadata is required: clients must announce coordinates (channelName + deliveryMode served by a registered transport) and capabilities.",
                    conversationId = request.ConversationId
                });
            }

            // Webhook-specific addressing gate: the push-style transport cannot route outbound
            // traffic without a reachable callback URL, so a handshake declaring deliveryMode=webhook
            // without an absolute http(s) URL is rejected here — the same shape as the generic gate
            // above, just narrower. The scheme is required too: on Unix a bare path such as "/hook"
            // parses as an absolute file URI, which nothing can POST to. Other transports (signalr,
            // future pull/duplex modes) leave CallbackUrl null; no requirement applies to them.
            string normalisedDeliveryMode = request.ChannelMetadata.Coordinates.DeliveryMode.Trim().ToLowerInvariant();
            if (normalisedDeliveryMode == Constants.DeliveryModes.Webhook
                 && !(Uri.TryCreate(request.ChannelMetadata.Coordinates.CallbackUrl, UriKind.Absolute, out Uri? callbackUri)
                      && (callbackUri.Scheme == Uri.UriSchemeHttp || callbackUri.Scheme == Uri.UriSchemeHttps)))
            {
                logger.LogWarning(
                    "Start requested for conversation {ConversationId} with deliveryMode=webhook but missing or invalid callbackUrl; returning 400",
                    request.ConversationId);
                return BadRequest(new
                {
                    error = "deliveryMode=webhook requires an absolute http(s) callbackUrl in channel coordinates.",
                    conversationId = request.ConversationId
                });
            }

            // Settled before answering, so the conversation exists on record the moment the client
            // learns it started: a message it sends straight away finds it, on this process or any other.
            await channelMetadataStore.RegisterChannelMetadataAsync(request.ConversationId, request.ChannelMetadata);

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                Constants.Actors.Manager, request.ConversationId);

            manager.Tell(new Records.CreateConversation(request.ConversationId));

            logger.LogInformation("Conversation creation queued: {RequestConversationId}", request.ConversationId);

            return Accepted(new StartConversationResponse(
                ConversationId: request.ConversationId,
                Message: "Conversation creation started"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start conversation");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Ends an existing conversation by stopping the ConversationManagerActor and its child actors.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to end</param>
    /// <returns>
    /// 200 OK on successful termination.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/end")]
    public async Task<IActionResult> EndConversation(string conversationId)
    {
        try
        {
            (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;

            logger.LogInformation("Ending conversation {ConversationId}", conversationId);

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                Constants.Actors.Manager, conversationId);

            manager.Tell(new Records.TerminateConversation(conversationId));

            logger.LogInformation("Ended conversation {ConversationId}", conversationId);

            return Ok(new { message = "Conversation ended" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end conversation {ConversationId}", conversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resumes an existing conversation for a client coming back to it: reports what it needs to
    /// redraw its state. Read-only: the conversation's actors come back with its next message.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to resume</param>
    /// <returns>
    /// 202 Accepted with conversation details and the active agent on record.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/resume")]
    public async Task<IActionResult> ResumeConversation(string conversationId)
    {
        try
        {
            (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;

            logger.LogInformation("Resuming conversation {ConversationId}", conversationId);

            // Honest resume semantics: if nothing was ever persisted for this conversationId
            // (stale client storage, wiped deployment, unknown id) report 404 instead of
            // queueing a restore on a non-existent conversation. Callers like Cauldron
            // already handle 404 by falling back to StartConversation cleanly, which avoids
            // materialising a phantom DB and, further upstream, an unnecessary second convId.
            if (!conversationPersistenceService.ConversationExists(conversationId))
            {
                logger.LogWarning("Resume requested for unknown conversation {ConversationId}; returning 404", conversationId);
                return NotFound(new { error = "Conversation not found", conversationId });
            }

            // Nothing is set up here: the conversation's actors come back with its first message and
            // take their state from its record, exactly as after a restart. The active agent is read
            // only to be reported to the client.
            string? lastActiveAgent = await conversationPersistenceService
                .GetMostRecentActiveAgentAsync(conversationId);

            logger.LogInformation(
                "Conversation resumed: {ConversationId} with active agent: {LastActiveAgent}", conversationId, lastActiveAgent);

            // The gauge as the client last saw it, so it is redrawn immediately on resume
            double? dustLevel = await dustLimitService.GetRemainingLevelAsync(conversationId);

            // If the resumed conversation is already dust-dead, hand the client the
            // canonical terminal message (the very same dustLimitingOptions.ErrorMessage
            // the message endpoint emits on a doomed send and EmitDustExhaustionAsync
            // emits at end of turn) so a page refresh can re-surface the lockout banner
            // up front, instead of letting the user rediscover it by firing a message
            // that is instantly rejected. dustLevel == 0.0 is exactly ratio >= 1.0,
            // i.e. the same over-budget boundary IsOverBudgetAsync gates on. Null
            // otherwise (including when dust limiting is disabled).
            string? dustExhaustedMessage = dustLevel is <= 0.0
                ? dustLimitingOptions.ErrorMessage
                : null;

            return Accepted(new ResumeConversationResponse(
                ConversationId: conversationId,
                Resumed: true,
                ActiveAgent: lastActiveAgent,
                DustLevel: dustLevel,
                DustExhaustedMessage: dustExhaustedMessage));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to resume conversation {ConversationId}", conversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Retrieves the complete conversation history for a given conversation ID.
    /// Returns all messages chronologically ordered across all participating agents.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <returns>
    /// 200 OK with a ConversationHistoryResponse wrapping the MorganaChatMessage array on success.
    /// 404 Not Found if conversation doesn't exist.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpGet("conversation/{conversationId}/history")]
    public async Task<IActionResult> GetConversationHistory(string conversationId)
    {
        try
        {
            (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;

            logger.LogInformation("Retrieving conversation history for {ConversationId}", conversationId);

            MorganaChatMessage[] chatMessages = await conversationPersistenceService
                .GetConversationHistoryAsync(conversationId);

            if (chatMessages.Length == 0)
            {
                logger.LogWarning("No history found for conversation {ConversationId}", conversationId);
                return NotFound(new { error = $"Conversation {conversationId} not found or has no messages" });
            }

            logger.LogInformation("Retrieved {ChatMessagesLength} messages for conversation {ConversationId}", chatMessages.Length, conversationId);

            // The gauge travels with the transcript: a client catching up on replies it missed
            // redraws it as the pushes it missed would have
            return Ok(new ConversationHistoryResponse(
                chatMessages,
                await dustLimitService.GetRemainingLevelAsync(conversationId)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve conversation history for {ConversationId}", conversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Sends a user message to the conversation for processing by the actor pipeline.
    /// Opens a morgana.turn OTel Activity and propagates its context into the actor system.
    /// The response will be delivered asynchronously via SignalR.
    /// </summary>
    /// <param name="request">Request containing conversation ID and message text</param>
    /// <returns>
    /// 202 Accepted immediately after message is queued.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure to queue message.
    /// </returns>
    [HttpPost("conversation/{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            #region Authentication
            (IActionResult? authFailure, string? callerId) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;
            #endregion

            #region Unknown Conversation
            // A message only continues a conversation that was started: it opens none. Checked
            // before the rate limiter, whose own bookkeeping would otherwise create the conversation's
            // database and with it a conversation that never had a handshake.
            if (!conversationPersistenceService.ConversationExists(request.ConversationId))
            {
                logger.LogWarning("Message for unknown conversation {RequestConversationId}; returning 404", request.ConversationId);
                return NotFound(new { error = "Conversation not found", conversationId = request.ConversationId });
            }
            #endregion

            #region Rate And Dust Limiting
            if (await RefuseOverRateOrDustLimitAsync(request.ConversationId) is { } limitRefusal)
                return limitRefusal;
            #endregion

            logger.LogInformation("Sending message to conversation {RequestConversationId}", request.ConversationId);

            // Capture the HTTP span context so the supervisor can link its turn span back to it.
            // The turn span itself is created and managed by ConversationSupervisorActor,
            // which keeps it open for the full pipeline duration (guard → classifier → agent).
            ActivityContext httpContext = Activity.Current?.Context ?? default;

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                Constants.Actors.Manager, request.ConversationId);

            manager.Tell(new Records.UserMessage(
                request.ConversationId,
                request.Text,
                DateTime.UtcNow,
                httpContext,           // passed as ActivityLink to turn span in supervisor
                callerId                 // authenticated caller identity
            ));

            logger.LogInformation("Message sent to conversation {RequestConversationId}", request.ConversationId);

            return Accepted(new
            {
                conversationId = request.ConversationId,
                message = "Message processing started",
                note = "Response will be sent via SignalR"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send message to conversation {RequestConversationId}", request.ConversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint for monitoring the conversation service and actor system status.
    /// </summary>
    /// <returns>503 KO with health failure information<br/>200 OK with health status information</returns>
    [HttpGet("health")]
    public IActionResult Health()
    {
        bool actorSystemAlive = !actorSystem.WhenTerminated.IsCompleted;

        if (!actorSystemAlive)
            return StatusCode(503, new
            {
                status = "unhealthy",
                reason = "Actor system terminated",
                actorSystem = actorSystem.Name,
                uptime = actorSystem.Uptime
            });

        return Ok(new
        {
            status = "healthy",
            actorSystem = actorSystem.Name,
            uptime = actorSystem.Uptime
        });
    }

    /// <summary>
    /// Lists the commands this installation executes on a conversation's behalf, for a channel to offer
    /// in its command palette next to its own.
    /// </summary>
    /// <returns>200 OK with a <see cref="CommandCatalogResponse"/>, empty when no command is installed.</returns>
    [HttpGet("commands")]
    public async Task<IActionResult> GetCommandCatalog()
    {
        (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
        if (authFailure is not null)
            return authFailure;

        // The same catalogue for every channel: which commands a channel shows is the channel's choice
        return Ok(new CommandCatalogResponse(commandRegistryService.GetCatalog()));
    }

    /// <summary>
    /// Runs one of the published commands on a conversation. The outcome reaches the user over the
    /// channel's transport, as a reply does. A command meets the rate and dust limits a message meets:
    /// it is a request the user fired and it may well spend tokens.
    /// </summary>
    /// <returns>
    /// 202 Accepted once the command has run.
    /// 400 Bad Request when no command answers to the name, when its options are not what it declares, when one that must be confirmed was not, or when one needing a desk finds none carrying the conversation.
    /// 404 Not Found if the conversation was never started.
    /// 429 Too Many Requests on the same limits a message meets.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/command")]
    public async Task<IActionResult> ExecuteCommand([FromBody] ExecuteCommandRequest request)
    {
        try
        {
            (IActionResult? authFailure, _) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;

            // A command acts on a conversation that was started and opens none, exactly as a message does:
            // checked before the rate limiter, whose bookkeeping would otherwise create its database
            if (!conversationPersistenceService.ConversationExists(request.ConversationId))
            {
                logger.LogWarning("Command for unknown conversation {RequestConversationId}; returning 404", request.ConversationId);
                return NotFound(new { error = "Conversation not found", conversationId = request.ConversationId });
            }

            // An unknown name is the channel's mistake, not the user's: nothing is pushed to the conversation
            if (commandRegistryService.ResolveCommand(request.Name) is not { } command)
            {
                logger.LogWarning("Unknown command '{CommandName}' for conversation {RequestConversationId}", request.Name, request.ConversationId);
                return BadRequest(new { error = "Unknown command", name = request.Name });
            }

            // A command acting on the desk carrying the conversation has nothing to act on while Morgana is
            // holding it herself: what she says in her own voice belongs to no desk, so summarizing or
            // otherwise reworking "the agent's" side there would reach the presentation and the refusals
            if (command.Descriptor.RequiresActiveAgent
                && await conversationPersistenceService.GetMostRecentActiveAgentAsync(request.ConversationId) is not { Length: > 0 })
            {
                logger.LogWarning("Command '{CommandName}' needs a desk carrying conversation {RequestConversationId}, which none is",
                    command.Descriptor.Name, request.ConversationId);
                return BadRequest(new { error = "Command requires an agent carrying the conversation", name = command.Descriptor.Name });
            }

            // A value the command never declared, or one it cannot work without, is refused before anything
            // reaches the conversation: the option is judged by the command's own rule, the one the channel used
            if (command.Descriptor.DescribeOptionProblem(request.Options) is { } optionProblem)
            {
                logger.LogWarning("Command '{CommandName}' was asked for with options it cannot take on conversation {RequestConversationId}: {OptionProblem}",
                    command.Descriptor.Name, request.ConversationId, optionProblem);
                return BadRequest(new { error = optionProblem, name = command.Descriptor.Name });
            }

            // A command that cannot be taken back runs only on a Yes the channel says it obtained. Nothing
            // reaches the conversation otherwise: a channel with no confirmation of its own simply cannot run one
            if (command.Descriptor.RequiresConfirmation && !request.Confirmed)
            {
                logger.LogWarning("Command '{CommandName}' needs confirmation and none was carried; refusing for conversation {RequestConversationId}",
                    command.Descriptor.Name, request.ConversationId);
                return BadRequest(new { error = "Command requires confirmation", name = command.Descriptor.Name });
            }

            // Resolved first: an unknown name is refused without counting against the user's limits
            if (await RefuseOverRateOrDustLimitAsync(request.ConversationId) is { } limitRefusal)
                return limitRefusal;

            // The command answers the user itself over the channel, so the HTTP reply only acknowledges it ran
            logger.LogInformation("Running command '{CommandName}' on conversation {RequestConversationId}", command.Descriptor.Name, request.ConversationId);
            await command.ExecuteAsync(request.ConversationId, request.Options ?? new Dictionary<string, string>());

            return Accepted(new { conversationId = request.ConversationId, command = command.Descriptor.Name });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run command '{CommandName}' on conversation {RequestConversationId}", request.Name, request.ConversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    #region Utilities
    /// <summary>
    /// Holds a request that would start work on <paramref name="conversationId"/> to the rate limit, then to
    /// the dust budget. Returns the 429 to answer with, after pushing the user-facing explanation over the
    /// channel. Null when the request may proceed.
    /// </summary>
    private async Task<IActionResult?> RefuseOverRateOrDustLimitAsync(string conversationId)
    {
        // Checking also records the request, so every call here counts towards the window whatever follows
        Records.RateLimitResult rateLimitResult = await rateLimitService.CheckAndRecordAsync(conversationId);
        if (!rateLimitResult.IsAllowed)
        {
            logger.LogWarning(
                "Rate limit exceeded for conversation {RequestConversationId}: {ViolatedLimit}", conversationId, rateLimitResult.ViolatedLimit);

            // The user hears why over the channel; the 429 below is for the client, which does not show it
            string rateLimitViolation = GetRateLimitErrorMessage(rateLimitResult);
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = rateLimitViolation,
                MessageType = "system_warning",
                ErrorReason = "rate_limit_exceeded",
                AgentName = "Morgana",
                AgentCompleted = false
            });

            // A window that reports no wait still gets a minute, so a client never retries in a tight loop
            Response.Headers.Append("Retry-After", rateLimitResult.RetryAfterSeconds?.ToString() ?? "60");
            return StatusCode(429, new
            {
                error = "Rate limit exceeded",
                violatedLimit = rateLimitResult.ViolatedLimit,
                retryAfterSeconds = rateLimitResult.RetryAfterSeconds,
                message = rateLimitViolation
            });
        }

        // Orthogonal to rate limiting: the rate limiter caps message frequency, the dust
        // limiter caps token consumption. Checked after it, same 429 shape. Once the
        // budget is spent the conversation is terminal — there is no continuation, the
        // user must start a brand-new conversation.
        if (await dustLimitService.IsOverBudgetAsync(conversationId))
        {
            logger.LogWarning(
                "Dust budget exhausted for conversation {RequestConversationId}", conversationId);

            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = dustLimitingOptions.ErrorMessage,
                MessageType = "error",
                ErrorReason = "dust_budget_exhausted",
                AgentName = "Morgana",
                AgentCompleted = false
            });

            return StatusCode(429, new
            {
                error = "Dust budget exhausted",
                message = dustLimitingOptions.ErrorMessage
            });
        }

        return null;
    }

    /// <summary>
    /// Validates the bearer token from the Authorization header when authentication is enabled.
    /// Returns null if the token is valid; returns an IActionResult (401) on failure.
    /// On success, outputs the authenticated CallerId.
    /// </summary>
    private async Task<(IActionResult? Failure, string? CallerId)> AuthenticateRequestAsync()
    {
        string? authorizationHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Authentication failed: missing or malformed Authorization header");
            return (Unauthorized(new { error = "Missing or malformed Authorization header. Expected: Bearer <token>" }), null);
        }

        string token = authorizationHeader["Bearer ".Length..].Trim();
        Records.AuthenticationResult authResult = await authenticationService.AuthenticateAsync(token);

        if (!authResult.IsAuthenticated)
        {
            logger.LogWarning("Authentication failed: {Error}", authResult.Error);
            return (Unauthorized(new { error = authResult.Error }), null);
        }

        // A caller is a channel or a colleague, never both. A partner's key was cut to consult this
        // installation's agents over A2A, where its inbound policy may hold it to a few desks; letting
        // it open a conversation here would hand it every agent back through the classifier, past the
        // very boundary that policy draws.
        if (authResult.IsPartner)
        {
            logger.LogWarning("Authentication rejected: issuer '{Issuer}' is a partner and the conversation API serves channels", authResult.Issuer);
            return (Unauthorized(new { error = "This API serves channels; a partner consults published agents over A2A." }), null);
        }

        return (null, authResult.CallerId);
    }

    /// <summary>
    /// Gets user-friendly error message for rate limit violations from configuration.
    /// Messages are customizable via appsettings.json (Morgana:RateLimiting section).
    /// Supports {limit} placeholder for displaying the actual limit value.
    /// </summary>
    private string GetRateLimitErrorMessage(Records.RateLimitResult result)
    {
        string message = result.ViolatedLimit switch
        {
            { } s when s.Contains("PerMinute") => rateLimitOptions.ErrorMessagePerMinute,
            { } s when s.Contains("PerHour")   => rateLimitOptions.ErrorMessagePerHour,
            { } s when s.Contains("PerDay")    => rateLimitOptions.ErrorMessagePerDay,
            _ => rateLimitOptions.ErrorMessageDefault
        };

        if (message.Contains("{limit}") && result.ViolatedLimit != null)
        {
            Match match = Regex.Match(result.ViolatedLimit, @"\((\d+)\)");
            if (match.Success)
                message = message.Replace("{limit}", match.Groups[1].Value);
        }

        return message;
    }
    #endregion
}