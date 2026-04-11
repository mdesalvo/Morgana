using System.Diagnostics;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
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
    private readonly IConversationPersistenceService conversationPersistenceService;
    private readonly IAuthenticationService authenticationService;
    private readonly Records.AuthenticationOptions authenticationOptions;
    private readonly IRateLimitService rateLimitService;
    private readonly Records.RateLimitOptions rateLimitOptions;

    // ==============================================================================
    /// <summary>
    /// Initializes a new instance of the MorganaController.
    /// </summary>
    /// <param name="actorSystem">Akka.NET actor system for conversation management</param>
    /// <param name="logger">Logger instance for diagnostic information</param>
    /// <param name="channelService">Outbound channel used to deliver system-level messages (e.g. rate-limit warnings) to the user</param>
    /// <param name="conversationPersistenceService">Service for recovering an existing conversation</param>
    /// <param name="authenticationService">Service for authenticating incoming requests</param>
    /// <param name="authenticationOptions">Options for configuration of the authentication service</param>
    /// <param name="rateLimitService">Service for rate limiting an existing conversation</param>
    /// <param name="rateLimitOptions">Options for configuration of the rate limiting service</param>
    public MorganaController(
        ActorSystem actorSystem,
        ILogger logger,
        IChannelService channelService,
        IConversationPersistenceService conversationPersistenceService,
        IAuthenticationService authenticationService,
        IOptions<Records.AuthenticationOptions> authenticationOptions,
        IRateLimitService rateLimitService,
        IOptions<Records.RateLimitOptions> rateLimitOptions)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.channelService = channelService;
        this.conversationPersistenceService = conversationPersistenceService;
        this.authenticationService = authenticationService;
        this.authenticationOptions = authenticationOptions.Value;
        this.rateLimitService = rateLimitService;
        this.rateLimitOptions = rateLimitOptions.Value;
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

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.CreateConversation(request.ConversationId, false, request.Capabilities));

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

            IActorRef manager = await actorSystem.GetOrCreateActorAsync<ConversationManagerActor>(
                "manager", conversationId);

            manager.Tell(new Records.CreateConversation(conversationId, true));

            // Get most recent active agent from database
            string? lastActiveAgent = await conversationPersistenceService
                .GetMostRecentActiveAgentAsync(conversationId);

            // Tell supervisor to restore active agent
            IActorRef supervisor = await actorSystem.GetOrCreateActorAsync<ConversationSupervisorActor>(
                "supervisor", conversationId);
            supervisor.Tell(new Records.RestoreActiveAgent(lastActiveAgent ?? "Morgana"));

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
    /// Returns null if authentication is disabled or the token is valid; returns an IActionResult (401) on failure.
    /// On success, outputs the authenticated UserId.
    /// </summary>
    private async Task<(IActionResult? Failure, string? UserId)> AuthenticateRequestAsync()
    {
        if (!authenticationOptions.Enabled)
            return (null, null);

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