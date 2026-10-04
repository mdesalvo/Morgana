using System.Diagnostics;
using Akka.Actor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Actors;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Contracts;
using Morgana.Web.Filters;

namespace Morgana.Web.Controllers;

/// <summary>
/// REST API for conversation lifecycle (start/end/resume/message/history) and message routing to actor system.
/// Integrates with SignalR for real-time bidirectional communication; OTel turn Activity boundary.
/// Validates channel metadata at start; authentication, known conversation and limits are filters.
/// </summary>
[ApiController]
[Route("api/morgana")]
[TypeFilter<ChannelAuthenticationFilter>]
[TypeFilter<FailureResponseFilter>]
public class MorganaController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IChannelServiceFactory channelServiceFactory;
    private readonly IChannelMetadataStore channelMetadataStore;
    private readonly IConversationPersistenceService conversationPersistenceService;
    private readonly IDustLimitService dustLimitService;
    private readonly Records.DustLimitingOptions dustLimitingOptions;

    /// <summary>
    /// Initializes controller with actor system, persistence, dust budget and channel factory.
    /// Validates channel metadata handshake and delivery mode at conversation start.
    /// </summary>
    public MorganaController(
        ActorSystem actorSystem,
        ILogger logger,
        IChannelServiceFactory channelServiceFactory,
        IChannelMetadataStore channelMetadataStore,
        IConversationPersistenceService conversationPersistenceService,
        IDustLimitService dustLimitService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.channelServiceFactory = channelServiceFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.conversationPersistenceService = conversationPersistenceService;
        this.dustLimitService = dustLimitService;
        this.dustLimitingOptions = dustLimitingOptions.Value;
    }

    /// <summary>
    /// Starts a new conversation: settles its handshake on record, then creates the
    /// ConversationManagerActor and triggers presentation generation.
    /// </summary>
    /// <param name="request">Request containing the conversation ID to start</param>
    /// <returns>
    /// 202 Accepted once the conversation exists on record: a message sent right after is served.
    /// 409 Conflict if a conversation with that ID is already on record.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/start")]
    public async Task<IActionResult> StartConversationAsync([FromBody] StartConversationRequest request)
    {
        logger.LogInformation("Starting conversation {RequestConversationId}", request.ConversationId);

        // Morgana refuses to host a conversation for a channel that does not announce
        // its identity, its capability budget AND a delivery mode that matches a concrete
        // transport registered in DI.
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
        // without an absolute http(s) URL is rejected here.
        // The scheme is required too: on Unix a bare path such as "/hook" parses as an absolute file URI,
        // which nothing can POST to. Other transports (e.g: signalr) leave CallbackUrl null: no requirement applies to them.
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

        // Start opens and never reopens: accepting a known id would rewrite its handshake, handing its
        // replies to whoever named it. A genuine channel mints a fresh id per attempt and never meets this
        if (conversationPersistenceService.ConversationExists(request.ConversationId))
        {
            logger.LogWarning("Start requested for conversation {ConversationId} already on record; returning 409", request.ConversationId);
            return Conflict(new
            {
                error = "A conversation with this id already exists: start opens a new conversation with a fresh id.",
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

    /// <summary>
    /// Ends an existing conversation by stopping the ConversationManagerActor and its child actors.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to end</param>
    /// <returns>
    /// 200 OK on successful termination.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/end")]
    public async Task<IActionResult> EndConversationAsync([FromRoute] string conversationId)
    {
        logger.LogInformation("Ending conversation {ConversationId}", conversationId);

        IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
            Constants.Actors.Manager, conversationId);

        manager.Tell(new Records.TerminateConversation(conversationId));

        logger.LogInformation("Ended conversation {ConversationId}", conversationId);

        return Ok(new { message = "Conversation ended" });
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
    // An unknown id (stale client storage, wiped deployment) is a 404: Cauldron falls back to starting anew
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    public async Task<IActionResult> ResumeConversationAsync([FromRoute] string conversationId)
    {
        logger.LogInformation("Resuming conversation {ConversationId}", conversationId);

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
        // canonical terminal message so that a page refresh can re-surface the
        // lockout banner up front, instead of letting the user rediscover it by
        // firing a message that is instantly rejected.
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
    public async Task<IActionResult> GetConversationHistoryAsync([FromRoute] string conversationId)
    {
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

    /// <summary>
    /// Sends a user message to the conversation for processing by the actor pipeline.
    /// Opens a morgana.turn OTel Activity and propagates its context into the actor system.
    /// The response will be delivered asynchronously via SignalR.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation the message continues</param>
    /// <param name="request">Request carrying the message text and its optional metadata</param>
    /// <returns>
    /// 202 Accepted immediately after message is queued.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure to queue message.
    /// </returns>
    [HttpPost("conversation/{conversationId}/message")]
    // A message only continues a conversation that was started: it opens none
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    [TypeFilter<ConversationLimitsFilter>(Order = 2)]
    public async Task<IActionResult> SendMessageAsync([FromRoute] string conversationId, [FromBody] SendMessageRequest request)
    {
        logger.LogInformation("Sending message to conversation {ConversationId}", conversationId);

        // Capture the HTTP span context so the supervisor can link its turn span back to it.
        // The turn span itself is created and managed by ConversationSupervisorActor,
        // which keeps it open for the full pipeline duration (guard → classifier → agent).
        ActivityContext httpContext = Activity.Current?.Context ?? default;

        IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
            Constants.Actors.Manager, conversationId);

        manager.Tell(new Records.UserMessage(
            conversationId,
            request.Text,
            DateTime.UtcNow,
            httpContext,           // passed as ActivityLink to turn span in supervisor
            HttpContext.Items[ChannelAuthenticationFilter.CallerIdItemKey] as string
        ));

        logger.LogInformation("Message sent to conversation {ConversationId}", conversationId);

        return Accepted(new
        {
            conversationId,
            message = "Message processing started",
            note = "Response will be sent via SignalR"
        });
    }

    /// <summary>
    /// Health check endpoint for monitoring the conversation service and actor system status.
    /// </summary>
    /// <returns>503 KO with health failure information<br/>200 OK with health status information</returns>
    [HttpGet("health")]
    [AllowAnonymous]
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
}