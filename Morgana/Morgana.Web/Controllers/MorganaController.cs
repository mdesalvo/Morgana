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
/// REST API of the conversation: start, end, resume, message, history and the health probe.
/// Authentication, the known-conversation check and the limits are MVC filters, run in the Order each action gives them;
/// the answers to a message reach the user over the channel, never in the HTTP reply.
/// </summary>
[ApiController]
[Route("api/morgana")]
[TypeFilter<ChannelAuthenticationFilter>]
[TypeFilter<FailureResponseFilter>]
public class MorganaController : ControllerBase
{
    /// <summary>Holds the conversations' actors, reached by name from the conversation id.</summary>
    private readonly ActorSystem actorSystem;

    /// <summary>Records each call and each refusal.</summary>
    private readonly ILogger logger;

    /// <summary>Tells whether the deliveryMode a channel announces is served by a registered transport.</summary>
    private readonly IChannelServiceFactory channelServiceFactory;

    /// <summary>Settles the handshake on record before start answers.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>The record: whether a conversation exists, its active agent and its transcript.</summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>The gauge a resuming or catching-up client redraws.</summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>The lockout text a resumed conversation with no dust left is handed.</summary>
    private readonly Records.DustLimitingOptions dustLimitingOptions;

    /// <summary>Seconds that every channel is told to wait on a silent turn.</summary>
    private readonly int replyTimeoutSeconds;

    /// <summary>Binds the controller to the actor system, the record and the channel services.</summary>
    public MorganaController(
        ActorSystem actorSystem,
        ILogger logger,
        IChannelServiceFactory channelServiceFactory,
        IChannelMetadataStore channelMetadataStore,
        IConversationPersistenceService conversationPersistenceService,
        IDustLimitService dustLimitService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        IConfiguration configuration)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.channelServiceFactory = channelServiceFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.conversationPersistenceService = conversationPersistenceService;
        this.dustLimitService = dustLimitService;
        this.dustLimitingOptions = dustLimitingOptions.Value;

        // The wait that every channel is told, read once from the ladder that orders every party's wait.
        replyTimeoutSeconds = (int)Records.TurnWaits.From(configuration).Channel.TotalSeconds;
    }

    /// <summary>Starts a new conversation: settles its handshake on record, then creates its manager, which has Morgana present herself.</summary>
    /// <param name="request">The conversation id the channel minted and its handshake.</param>
    /// <returns>
    /// 202 Accepted once the conversation exists on record: a message sent right after is served.
    /// 400 Bad Request if the ID is not a 32-digit GUID or the channel metadata is incomplete.
    /// 409 Conflict if a conversation with that ID is already on record.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/start")]
    public async Task<IActionResult> StartConversationAsync([FromBody] StartConversationRequest request)
    {
        logger.LogInformation("Starting conversation {RequestConversationId}", request.ConversationId);

        // The id becomes an actor name and a database file name: a GUID in its 32-digit format is the
        // one shape both take as is. Anything else would break Akka.NET only once the actors are raised.
        if (!Guid.TryParseExact(request.ConversationId, "N", out _))
        {
            logger.LogWarning("Start requested for conversation {ConversationId} not named by a 32-digit GUID; returning 400", request.ConversationId);
            return BadRequest(new
            {
                error = "The conversation id must be a GUID in its 32-digit format (no hyphens or braces).",
                conversationId = request.ConversationId
            });
        }

        // Morgana refuses to host a conversation for a channel that does not announce its identity, its
        // capabilities and a delivery mode that a registered transport serves: nothing could reach it.
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

        // A webhook channel is reached only at its callback, so a handshake declaring deliveryMode=webhook without an
        // absolute http(s) URL is refused here. The scheme is checked too: on Unix a bare path such as "/hook" parses
        // as an absolute file URI, which nothing can POST to. Other transports leave CallbackUrl null.
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
        if (await conversationPersistenceService.ConversationExistsAsync(request.ConversationId))
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

        // The manager is the conversation's entry point, created under the name every later call reaches it by.
        IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
            Constants.Actors.Manager, request.ConversationId);

        // The welcome is generated and sent over the channel while start has already answered.
        manager.Tell(new Records.CreateConversation(request.ConversationId));

        logger.LogInformation("Conversation creation queued: {RequestConversationId}", request.ConversationId);

        // 202: the conversation exists on record, while its welcome is still on the way over the channel.
        return Accepted(new StartConversationResponse(
            ConversationId: request.ConversationId,
            Message: "Conversation creation started"));
    }

    /// <summary>Ends a conversation by stopping its manager, which stops every actor beneath it.</summary>
    /// <param name="conversationId">The conversation to end.</param>
    /// <returns>
    /// 200 OK on successful termination, also when no actor of the conversation is running.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/end")]
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    public async Task<IActionResult> EndConversationAsync([FromRoute] string conversationId)
    {
        logger.LogInformation("Ending conversation {ConversationId}", conversationId);

        // Only a running manager has anything to stop. A conversation already ended is left as it is, as is one served
        // by a process that has restarted since: neither is brought up only to be torn down.
        // The manager stops the supervisor and with it every actor of the conversation.
        IActorRef? manager = await actorSystem.FindActorAsync(Constants.Actors.Manager, conversationId);
        manager?.Tell(new Records.TerminateConversation(conversationId));

        logger.LogInformation("Ended conversation {ConversationId}", conversationId);

        // The end is confirmed to the caller, since the actors are already being stopped.
        return Ok(new { message = "Conversation ended" });
    }

    /// <summary>
    /// Reports to a client coming back to a conversation what it needs to redraw; read-only, since the actors come back with the next message.
    /// An unknown id, from stale client storage or a wiped deployment, is a 404 on which Cauldron starts anew.
    /// </summary>
    /// <param name="conversationId">The conversation to resume.</param>
    /// <returns>
    /// 202 Accepted with conversation details and the active agent on record.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/resume")]
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

        // A conversation with no dust left hands the client its lockout text, so a refreshed page shows the
        // lockout up front instead of letting the user rediscover it with a message that is refused at once.
        string? dustExhaustedMessage = dustLevel is <= 0.0
            ? dustLimitingOptions.ErrorMessage
            : null;

        // Everything a client redraws: the active agent's name, the gauge, the wait on a silent turn and the lockout.
        return Accepted(new ResumeConversationResponse(
            ConversationId: conversationId,
            Resumed: true,
            ActiveAgent: lastActiveAgent,
            ConversationMetadata: new ConversationMetadata(dustLevel, replyTimeoutSeconds),
            DustExhaustedMessage: dustExhaustedMessage));
    }

    /// <summary>
    /// Returns the conversation's transcript across every participant, in chronological order.
    /// A started conversation with nothing said yet is an empty transcript, never one that does not exist.
    /// </summary>
    /// <param name="conversationId">The conversation whose transcript is read.</param>
    /// <returns>
    /// 200 OK with a ConversationHistoryResponse wrapping the MorganaChatMessage array, empty while nobody has spoken.
    /// 404 Not Found if the conversation was never started.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpGet("conversation/{conversationId}/history")]
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    public async Task<IActionResult> GetConversationHistoryAsync([FromRoute] string conversationId)
    {
        logger.LogInformation("Retrieving conversation history for {ConversationId}", conversationId);

        // The transcript across every participant, in chronological order.
        MorganaChatMessage[] chatMessages = await conversationPersistenceService
            .GetConversationHistoryAsync(conversationId);

        logger.LogInformation("Retrieved {ChatMessagesLength} messages for conversation {ConversationId}", chatMessages.Length, conversationId);

        // The conversation's description travels with the transcript: a client catching up on replies it missed
        // redraws the gauge and learns the wait as the pushes it missed would have told it
        return Ok(new ConversationHistoryResponse(
            chatMessages,
            new ConversationMetadata(await dustLimitService.GetRemainingLevelAsync(conversationId), replyTimeoutSeconds)));
    }

    /// <summary>
    /// Hands a user's message to the conversation's pipeline, whose answer reaches the user over the channel.
    /// A message only continues a conversation that was started and opens none; the limits come after that check.
    /// </summary>
    /// <param name="conversationId">The conversation the message continues.</param>
    /// <param name="request">The message's text.</param>
    /// <returns>
    /// 202 Accepted immediately after message is queued.
    /// 404 Not Found if the conversation was never started.
    /// 429 Too Many Requests past the rate limit or once the dust budget is spent.
    /// 500 Internal Server Error on failure to queue message.
    /// </returns>
    [HttpPost("conversation/{conversationId}/message")]
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    [TypeFilter<ConversationLimitsFilter>(Order = 2)]
    public async Task<IActionResult> SendMessageAsync([FromRoute] string conversationId, [FromBody] SendMessageRequest request)
    {
        logger.LogInformation("Sending message to conversation {ConversationId}", conversationId);

        // The HTTP span's context, which the supervisor links its turn span to: the turn outlives this request,
        // since the supervisor keeps its span open for the whole pipeline.
        ActivityContext httpSpanContext = Activity.Current?.Context ?? default;

        // A conversation whose actors stopped gets its manager back here, as does one that a restarted process never raised.
        IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
            Constants.Actors.Manager, conversationId);

        // The caller is the one the channel's token proved, never one the body claims.
        manager.Tell(new Records.UserMessage(
            conversationId,
            request.Text,
            DateTime.UtcNow,
            httpSpanContext,
            HttpContext.Items[ChannelAuthenticationFilter.CallerIdItemKey] as string
        ));

        logger.LogInformation("Message sent to conversation {ConversationId}", conversationId);

        // 202 before any answer exists: the pipeline runs on and its answer travels over the channel's transport.
        return Accepted(new
        {
            conversationId,
            message = "Message processing started",
            note = "Response will be sent over the channel"
        });
    }

    /// <summary>
    /// Answers the liveness probe from the actor system's state, without a token.
    /// The probe is asked by infrastructure that holds no channel key, which is why it alone is anonymous.
    /// </summary>
    /// <returns>200 OK while the actor system runs, 503 Service Unavailable once it has terminated.</returns>
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        // A terminated actor system serves no conversation, whatever the web host still answers.
        bool actorSystemAlive = !actorSystem.WhenTerminated.IsCompleted;

        // The orchestrator wants a restart: no turn can run.
        if (!actorSystemAlive)
            return StatusCode(503, new
            {
                status = "unhealthy",
                reason = "Actor system terminated",
                actorSystem = actorSystem.Name,
                uptime = actorSystem.Uptime
            });

        // Alive, with the uptime an operator reads to spot a restart loop.
        return Ok(new
        {
            status = "healthy",
            actorSystem = actorSystem.Name,
            uptime = actorSystem.Uptime
        });
    }
}