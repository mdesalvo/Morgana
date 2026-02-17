using System.Diagnostics;
using System.Text.RegularExpressions;
using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Morgana.Framework;
using Morgana.Framework.Actors;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;
using Morgana.Framework.Telemetry;
using Morgana.SignalR.Hubs;
using Morgana.SignalR.Messages;

namespace Morgana.SignalR.Controllers;

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
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IHubContext<MorganaHub> signalrContext;
    private readonly IConversationPersistenceService conversationPersistenceService;
    private readonly IRateLimitService rateLimitService;
    private readonly Records.RateLimitOptions rateLimitOptions;

    // ==============================================================================
    /// <summary>
    /// Initializes a new instance of the ConversationController.
    /// </summary>
    /// <param name="actorSystem">Akka.NET actor system for conversation management</param>
    /// <param name="logger">Logger instance for diagnostic information</param>
    /// <param name="signalrContext">SignalR hub context for real-time client communication</param>
    /// <param name="conversationPersistenceService">Service for recovering an existing conversation</param>
    /// <param name="rateLimitService">Service for rate limiting an existing conversation</param>
    /// <param name="rateLimitOptions">Options for configuration of the rate limiting service</param>
    public ConversationController(
        ActorSystem actorSystem,
        ILogger logger,
        IHubContext<MorganaHub> signalrContext,
        IConversationPersistenceService conversationPersistenceService,
        IRateLimitService rateLimitService,
        IOptions<Records.RateLimitOptions> rateLimitOptions)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.signalrContext = signalrContext;
        this.conversationPersistenceService = conversationPersistenceService;
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
    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] Records.StartConversationRequest request)
    {
        try
        {
            logger.LogInformation($"Starting conversation {request.ConversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.CreateConversation(request.ConversationId, false));

            logger.LogInformation($"Conversation creation queued: {request.ConversationId}");

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
    [HttpPost("{conversationId}/end")]
    public async Task<IActionResult> EndConversation(string conversationId)
    {
        try
        {
            logger.LogInformation($"Ending conversation {conversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", conversationId);

            manager.Tell(new Records.TerminateConversation(conversationId));

            logger.LogInformation($"Ended conversation {conversationId}");

            return Ok(new { message = "Conversation ended" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to end conversation {conversationId}");
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
    [HttpPost("{conversationId}/resume")]
    public async Task<IActionResult> ResumeConversation(string conversationId)
    {
        try
        {
            logger.LogInformation($"Resuming conversation {conversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", conversationId);

            manager.Tell(new Records.CreateConversation(conversationId, true));

            // Get most recent active agent from database
            string? lastActiveAgent = await conversationPersistenceService
                .GetMostRecentActiveAgentAsync(conversationId);

            // Tell supervisor to restore active agent
            IActorRef supervisor = await actorSystem.GetOrCreateActor<ConversationSupervisorActor>(
                "supervisor", conversationId);
            supervisor.Tell(new Records.RestoreActiveAgent(lastActiveAgent ?? "Morgana"));

            logger.LogInformation(
                $"Conversation resume queued: {conversationId} with active agent: {lastActiveAgent}");

            return Accepted(new
            {
                conversationId = conversationId,
                resumed = true,
                activeAgent = lastActiveAgent
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to resume conversation {conversationId}");
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
    [HttpGet("{conversationId}/history")]
    public async Task<IActionResult> GetConversationHistory(string conversationId)
    {
        try
        {
            logger.LogInformation($"Retrieving conversation history for {conversationId}");

            Records.MorganaChatMessage[] chatMessages = await conversationPersistenceService
                .GetConversationHistoryAsync(conversationId);

            if (chatMessages.Length == 0)
            {
                logger.LogWarning($"No history found for conversation {conversationId}");
                return NotFound(new { error = $"Conversation {conversationId} not found or has no messages" });
            }

            logger.LogInformation($"Retrieved {chatMessages.Length} messages for conversation {conversationId}");

            return Ok(new { messages = chatMessages });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to retrieve conversation history for {conversationId}");
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
    [HttpPost("{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] Records.SendMessageRequest request)
    {
        try
        {
            #region Rate Limiting
            Records.RateLimitResult rateLimitResult = await rateLimitService.CheckAndRecordAsync(request.ConversationId);
            if (!rateLimitResult.IsAllowed)
            {
                logger.LogWarning(
                    $"Rate limit exceeded for conversation {request.ConversationId}: {rateLimitResult.ViolatedLimit}");

                string rateLimitViolation = GetRateLimitErrorMessage(rateLimitResult);
                await signalrContext.Clients
                    .Group(request.ConversationId)
                    .SendAsync("ReceiveMessage", new SignalRMessage
                    {
                        ConversationId = request.ConversationId,
                        Text = rateLimitViolation,
                        Timestamp = DateTime.UtcNow,
                        MessageType = "system_warning",
                        ErrorReason = "rate_limit_exceeded",
                        AgentName = "Morgana",
                        AgentCompleted = false,
                        QuickReplies = null
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

            logger.LogInformation($"Sending message to conversation {request.ConversationId}");

            // Open a turn span. No parent needed: all turns of the same conversation share
            // the conversationId attribute, which is sufficient to correlate them in any OTel backend.
            using Activity? turnActivity = MorganaTelemetry.Source.StartActivity(
                MorganaTelemetry.TurnActivity,
                ActivityKind.Internal);

            turnActivity?.SetTag(MorganaTelemetry.ConversationId, request.ConversationId);
            turnActivity?.SetTag(MorganaTelemetry.TurnUserMessage,
                request.Text.Length > 200 ? request.Text[..200] : request.Text);

            // Capture context before entering actor system (Activity.Current may differ on actor threads)
            ActivityContext turnContext = turnActivity?.Context ?? default;

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.UserMessage(
                request.ConversationId,
                request.Text,
                DateTime.UtcNow,
                turnContext           // propagate OTel context into actor pipeline
            ));

            logger.LogInformation($"Message sent to conversation {request.ConversationId}");

            return Accepted(new
            {
                conversationId = request.ConversationId,
                message = "Message processing started",
                note = "Response will be sent via SignalR"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to send message to conversation {request.ConversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Health check endpoint for monitoring the conversation service and actor system status.
    /// </summary>
    /// <returns>200 OK with health status information.</returns>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            actorSystem = actorSystem.Name,
            uptime = actorSystem.Uptime
        });
    }

    #region Utilities
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