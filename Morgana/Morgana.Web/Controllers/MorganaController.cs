using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Actors;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Controllers;

/// <summary>
/// REST API controller for managing Morgana conversation lifecycle and message routing.
/// Provides endpoints for starting/ending conversations and sending messages to the actor system.
/// Works in conjunction with SignalR for real-time bi-directional communication.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Overview:</strong></para>
/// <list type="bullet">
/// <item><term>HTTP endpoints</term><description>Handle conversation management (start/end) and message submission</description></item>
/// <item><term>Actor system integration</term><description>Creates and communicates with ConversationManagerActor instances</description></item>
/// <item><term>SignalR coordination</term><description>Messages are sent via HTTP, responses arrive via SignalR Hub</description></item>
/// </list>
/// <para><strong>Message Flow:</strong></para>
/// <para>Client → HTTP POST → Controller → ConversationManagerActor → Actor Pipeline → SignalR Hub → Client</para>
/// <para><strong>OpenTelemetry:</strong></para>
/// <para>The controller is the OTel boundary. On each SendMessage it opens a morgana.turn Activity
/// and propagates its context into the actor system via UserMessage.TurnContext, so that guard,
/// classifier, router, and agent actors can open correctly-parented child spans despite Akka.NET
/// breaking ambient Activity.Current across thread pool boundaries.</para>
/// </remarks>
[ApiController]
[Route("api/morgana")]
public class MorganaController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IChannelService channelService;
    private readonly IChannelServiceFactory channelServiceFactory;
    private readonly IConversationPersistenceService conversationPersistenceService;
    private readonly IAuthenticationService authenticationService;
    private readonly IRateLimitService rateLimitService;
    private readonly Records.RateLimitOptions rateLimitOptions;
    private readonly IDustLimitService dustLimitService;
    private readonly Records.DustLimitingOptions dustLimitingOptions;
    private readonly ILLMService llmService;
    private readonly IConfiguration configuration;

    // ==============================================================================
    /// <summary>
    /// Initializes a new instance of the MorganaController.
    /// </summary>
    /// <param name="actorSystem">Akka.NET actor system for conversation management</param>
    /// <param name="logger">Logger instance for diagnostic information</param>
    /// <param name="channelService">Outbound channel used to deliver system-level messages (e.g. rate-limit warnings) to the user</param>
    /// <param name="channelServiceFactory">Factory consulted at the start-conversation gate to reject handshakes whose declared deliveryMode has no concrete transport registered</param>
    /// <param name="conversationPersistenceService">Service for recovering an existing conversation</param>
    /// <param name="authenticationService">Service for authenticating incoming requests</param>
    /// <param name="rateLimitService">Service for rate limiting an existing conversation</param>
    /// <param name="rateLimitOptions">Options for configuration of the rate limiting service</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="dustLimitingOptions">Dust-limiting policy and message templates</param>
    /// <param name="llmService">LLM service used to compress a seed conversation's history</param>
    /// <param name="configuration">Configuration (read for the seed summarization prompt)</param>
    public MorganaController(
        ActorSystem actorSystem,
        ILogger logger,
        IChannelService channelService,
        IChannelServiceFactory channelServiceFactory,
        IConversationPersistenceService conversationPersistenceService,
        IAuthenticationService authenticationService,
        IRateLimitService rateLimitService,
        IOptions<Records.RateLimitOptions> rateLimitOptions,
        IDustLimitService dustLimitService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        ILLMService llmService,
        IConfiguration configuration)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.channelService = channelService;
        this.channelServiceFactory = channelServiceFactory;
        this.conversationPersistenceService = conversationPersistenceService;
        this.authenticationService = authenticationService;
        this.rateLimitService = rateLimitService;
        this.rateLimitOptions = rateLimitOptions.Value;
        this.dustLimitService = dustLimitService;
        this.dustLimitingOptions = dustLimitingOptions.Value;
        this.llmService = llmService;
        this.configuration = configuration;
    }

    /// <summary>
    /// Starts a new conversation by creating a ConversationManagerActor and triggering presentation generation.
    /// </summary>
    /// <param name="request">Request containing the conversation ID to start</param>
    /// <returns>
    /// 202 Accepted with conversation details immediately.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/start")]
    public async Task<IActionResult> StartConversation([FromBody] Records.StartConversationRequest request)
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
            // without a well-formed absolute URL is rejected here — the same shape as the generic
            // gate above, just narrower. Other transports (signalr, future pull/duplex modes) leave
            // CallbackUrl null; no requirement applies to them.
            string normalisedDeliveryMode = request.ChannelMetadata.Coordinates.DeliveryMode.Trim().ToLowerInvariant();
            if (normalisedDeliveryMode == "webhook"
                 && (string.IsNullOrWhiteSpace(request.ChannelMetadata.Coordinates.CallbackUrl)
                     || !Uri.TryCreate(request.ChannelMetadata.Coordinates.CallbackUrl, UriKind.Absolute, out _)))
            {
                logger.LogWarning(
                    "Start requested for conversation {ConversationId} with deliveryMode=webhook but missing or invalid callbackUrl; returning 400",
                    request.ConversationId);
                return BadRequest(new
                {
                    error = "deliveryMode=webhook requires a well-formed absolute callbackUrl in channel coordinates.",
                    conversationId = request.ConversationId
                });
            }

            // Memory seed: when continuing from a budget-exhausted conversation, compress its
            // history into a summary and carry over its shared context. Best-effort — a
            // failure here must not block the new conversation, it just starts vanilla.
            string? seedSummary = null;
            if (!string.IsNullOrWhiteSpace(request.SeedConversationId))
                seedSummary = await BuildSeedAsync(request.SeedConversationId, request.ConversationId);

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.CreateConversation(
                request.ConversationId, false, request.ChannelMetadata, seedSummary));

            logger.LogInformation("Conversation creation queued: {RequestConversationId}", request.ConversationId);

            return Accepted(new
            {
                conversationId = request.ConversationId,
                message = "Conversation creation started"
            });
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
                "manager", conversationId);

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
    /// Resumes an existing conversation by restoring actor hierarchy and active agent state from persistent storage.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to resume</param>
    /// <returns>
    /// 202 Accepted with conversation details and restored active agent immediately.
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

            // Resume the manager owning this conversation
            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                "manager", conversationId);

            // Send it the message to restore this conversation
            manager.Tell(new Records.CreateConversation(
                conversationId, true));

            // Get most recent active agent from database
            string? lastActiveAgent = await conversationPersistenceService
                .GetMostRecentActiveAgentAsync(conversationId);

            // Tell manager to restore active agent, if found
            if (!string.IsNullOrWhiteSpace(lastActiveAgent))
                manager.Tell(new Records.RestoreActiveAgent(lastActiveAgent));

            logger.LogInformation(
                "Conversation resume queued: {ConversationId} with active agent: {LastActiveAgent}", conversationId, lastActiveAgent);

            return Accepted(new
            {
                conversationId = conversationId,
                resumed = true,
                activeAgent = lastActiveAgent
            });
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
    /// 200 OK with array of MorganaChatMessage on success.
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

            Records.MorganaChatMessage[] chatMessages = await conversationPersistenceService
                .GetConversationHistoryAsync(conversationId);

            if (chatMessages.Length == 0)
            {
                logger.LogWarning("No history found for conversation {ConversationId}", conversationId);
                return NotFound(new { error = $"Conversation {conversationId} not found or has no messages" });
            }

            logger.LogInformation("Retrieved {ChatMessagesLength} messages for conversation {ConversationId}", chatMessages.Length, conversationId);

            return Ok(new { messages = chatMessages });
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
    /// 500 Internal Server Error on failure to queue message.
    /// </returns>
    [HttpPost("conversation/{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] Records.SendMessageRequest request)
    {
        try
        {
            #region Authentication
            (IActionResult? authFailure, string? userId) = await AuthenticateRequestAsync();
            if (authFailure is not null)
                return authFailure;
            #endregion

            #region Rate Limiting
            Records.RateLimitResult rateLimitResult = await rateLimitService.CheckAndRecordAsync(request.ConversationId);
            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning(
                    "Rate limit exceeded for conversation {RequestConversationId}: {ViolatedLimit}", request.ConversationId, rateLimitResult.ViolatedLimit);

                string rateLimitViolation = GetRateLimitErrorMessage(rateLimitResult);
                await channelService.SendMessageAsync(new Records.ChannelMessage
                {
                    ConversationId = request.ConversationId,
                    Text = rateLimitViolation,
                    MessageType = "system_warning",
                    ErrorReason = "rate_limit_exceeded",
                    AgentName = "Morgana",
                    AgentCompleted = false
                });

                Response.Headers.Append("Retry-After", rateLimitResult.RetryAfterSeconds?.ToString() ?? "60");
                return StatusCode(429, new
                {
                    error = "Rate limit exceeded",
                    violatedLimit = rateLimitResult.ViolatedLimit,
                    retryAfterSeconds = rateLimitResult.RetryAfterSeconds,
                    message = rateLimitViolation
                });
            }
            #endregion

            #region Dust Limiting
            // Orthogonal to rate limiting: the rate limiter caps message frequency, the dust
            // limiter caps token consumption. Checked after it, same 429 shape. The error
            // carries a quick reply CTA — the AdaptingChannelService degrades it to plain
            // text for channels that do not support quick replies (e.g. Rune).
            if (await dustLimitService.IsOverBudgetAsync(request.ConversationId))
            {
                logger.LogWarning(
                    "Dust budget exhausted for conversation {RequestConversationId}", request.ConversationId);

                await channelService.SendMessageAsync(new Records.ChannelMessage
                {
                    ConversationId = request.ConversationId,
                    Text = dustLimitingOptions.ErrorMessage,
                    MessageType = "error",
                    ErrorReason = "dust_budget_exhausted",
                    AgentName = "Morgana",
                    AgentCompleted = false,
                    QuickReplies =
                    [
                        new Records.QuickReply(
                            "seed_conversation",
                            "✨ Continue in a new conversation",
                            "seed_conversation")
                    ]
                });

                return StatusCode(429, new
                {
                    error = "Dust budget exhausted",
                    message = dustLimitingOptions.ErrorMessage
                });
            }
            #endregion

            logger.LogInformation("Sending message to conversation {RequestConversationId}", request.ConversationId);

            // Capture the HTTP span context so the supervisor can link its turn span back to it.
            // The turn span itself is created and managed by ConversationSupervisorActor,
            // which keeps it open for the full pipeline duration (guard → classifier → agent).
            ActivityContext httpContext = Activity.Current?.Context ?? default;

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.UserMessage(
                request.ConversationId,
                request.Text,
                DateTime.UtcNow,
                httpContext,           // passed as ActivityLink to turn span in supervisor
                userId                 // authenticated caller identity
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

    #region Utilities
    /// <summary>
    /// Validates the bearer token from the Authorization header when authentication is enabled.
    /// Returns null if the token is valid; returns an IActionResult (401) on failure.
    /// On success, outputs the authenticated UserId.
    /// </summary>
    private async Task<(IActionResult? Failure, string? UserId)> AuthenticateRequestAsync()
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

        return (null, authResult.UserId);
    }

    /// <summary>
    /// Builds the memory seed for a "continue in a new conversation" start: compresses the
    /// exhausted conversation's history into a single summary (surfaced to the user by the
    /// manager actor) and copies its shared context into the new conversation so extracted
    /// facts (userId, contract numbers, …) are not lost.
    /// </summary>
    /// <returns>The summary text, or null if there is nothing to seed (no history / failure).
    /// Failures are swallowed: the new conversation simply starts vanilla.</returns>
    private async Task<string?> BuildSeedAsync(string seedConversationId, string newConversationId)
    {
        try
        {
            if (!conversationPersistenceService.ConversationExists(seedConversationId))
            {
                logger.LogWarning(
                    "Seed requested from unknown conversation {SeedConversationId}; starting vanilla",
                    seedConversationId);
                return null;
            }

            // Carry over shared context (first-write-wins is enforced by the persistence layer).
            Dictionary<string, object> sharedVariables =
                await conversationPersistenceService.LoadSharedVariablesAsync(seedConversationId);
            foreach (KeyValuePair<string, object> variable in sharedVariables)
                await conversationPersistenceService.UpsertSharedVariableAsync(
                    newConversationId, variable.Key, variable.Value, "seed");

            // Compress the prior history into one summary using the configured prompt.
            Records.MorganaChatMessage[] history =
                await conversationPersistenceService.GetConversationHistoryAsync(seedConversationId);
            if (history.Length == 0)
                return null;

            StringBuilder transcript = new StringBuilder();
            foreach (Records.MorganaChatMessage message in history)
                transcript.AppendLine($"{message.AgentName}: {message.Text}");

            string summarizationPrompt =
                configuration["Morgana:HistoryReducer:SummarizationPrompt"]
                ?? "Summarize this conversation concisely, preserving user IDs, numbers, " +
                   "amounts, dates, decisions and unresolved issues.";

            string summary = await llmService.CompleteWithSystemPromptAsync(
                newConversationId, summarizationPrompt, transcript.ToString());

            if (string.IsNullOrWhiteSpace(summary))
                return null;

            // Exposed to the user, not silent system context: Morgana wakes with a minimal
            // conscious state, the user sees it remembers rather than starting from zero.
            return $"{dustLimitingOptions.SeedSummaryPrefix} {summary.Trim()}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to build seed from {SeedConversationId} for {NewConversationId}; starting vanilla",
                seedConversationId, newConversationId);
            return null;
        }
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