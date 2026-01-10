using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.ActorsFramework.Actors;
using Morgana.ActorsFramework.Extensions;
using Morgana.Foundations;
using Morgana.Startup.Hubs;

namespace Morgana.Startup.Controllers;

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
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IHubContext<ConversationHub> signalrContext;

    /// <summary>
    /// Initializes a new instance of the ConversationController.
    /// </summary>
    /// <param name="actorSystem">Akka.NET actor system for conversation management</param>
    /// <param name="logger">Logger instance for diagnostic information</param>
    /// <param name="signalrContext">SignalR hub context for real-time client communication</param>
    public ConversationController(
        ActorSystem actorSystem,
        ILogger logger,
        IHubContext<ConversationHub> signalrContext)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.signalrContext = signalrContext;
    }

    /// <summary>
    /// Starts a new conversation by creating a ConversationManagerActor and triggering presentation generation.
    /// </summary>
    /// <param name="request">Request containing the conversation ID to start</param>
    /// <returns>
    /// 200 OK with conversation details on success.
    /// 500 Internal Server Error on failure.
    /// </returns>
    /// <remarks>
    /// <para>This endpoint:</para>
    /// <list type="number">
    /// <item>Creates or retrieves the ConversationManagerActor for the given conversation ID</item>
    /// <item>Sends a CreateConversation message to the manager (which creates the supervisor)</item>
    /// <item>Waits for confirmation that the conversation was created</item>
    /// <item>The supervisor automatically generates and sends a presentation message via SignalR</item>
    /// </list>
    /// <para><strong>Client Flow:</strong> Call this endpoint, then listen on SignalR for the presentation message.</para>
    /// </remarks>
    /// <response code="200">Conversation started successfully</response>
    /// <response code="500">Internal error occurred</response>
    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] Records.StartConversationRequest request)
    {
        try
        {
            logger.LogInformation($"Starting conversation {request.ConversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", request.ConversationId);

            Records.ConversationCreated? conversationCreated = await manager.Ask<Records.ConversationCreated>(
                new Records.CreateConversation(request.ConversationId));

            logger.LogInformation($"Started conversation {conversationCreated.ConversationId}");

            return Ok(new
            {
                conversationId = conversationCreated.ConversationId,
                message = "Conversation started successfully"
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
    /// <remarks>
    /// <para>This endpoint:</para>
    /// <list type="number">
    /// <item>Retrieves the ConversationManagerActor for the given conversation ID</item>
    /// <item>Sends a TerminateConversation message (fire-and-forget with Tell)</item>
    /// <item>The manager stops the supervisor and all child actors (guard, classifier, router, agents)</item>
    /// </list>
    /// <para><strong>Note:</strong> This is a fire-and-forget operation. The actor system handles cleanup asynchronously.</para>
    /// </remarks>
    /// <response code="200">Conversation ended successfully</response>
    /// <response code="500">Internal error occurred</response>
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
    /// Sends a user message to the conversation for processing by the actor pipeline.
    /// The response will be delivered asynchronously via SignalR.
    /// </summary>
    /// <param name="request">Request containing conversation ID and message text</param>
    /// <returns>
    /// 202 Accepted immediately after message is queued.
    /// 500 Internal Server Error on failure to queue message.
    /// </returns>
    /// <remarks>
    /// <para><strong>Async Message Processing:</strong></para>
    /// <list type="number">
    /// <item>Message is sent to ConversationManagerActor (fire-and-forget with Tell)</item>
    /// <item>Actor pipeline processes: Guard → Classifier → Router → Agent</item>
    /// <item>Response is sent back to client via SignalR Hub</item>
    /// </list>
    /// <para><strong>Client Flow:</strong></para>
    /// <code>
    /// 1. POST /api/conversation/{id}/message (returns 202 Accepted)
    /// 2. Listen on SignalR for "ReceiveMessage" event
    /// 3. Receive AI response with metadata (agent name, completion status)
    /// </code>
    /// <para><strong>Note:</strong> HTTP 202 Accepted indicates the message was queued, not processed.
    /// Actual processing status is communicated via SignalR.</para>
    /// </remarks>
    /// <response code="202">Message queued for processing, response will arrive via SignalR</response>
    /// <response code="500">Internal error occurred</response>
    [HttpPost("{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] Records.SendMessageRequest request)
    {
        try
        {
            logger.LogInformation($"Sending message to conversation {request.ConversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>(
                "manager", request.ConversationId);

            manager.Tell(new Records.UserMessage(
                request.ConversationId,
                request.Text,
                DateTime.UtcNow
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
    /// <returns>
    /// 200 OK with health status information.
    /// </returns>
    /// <remarks>
    /// <para>Returns:</para>
    /// <list type="bullet">
    /// <item><term>status</term><description>Always "healthy" if endpoint responds</description></item>
    /// <item><term>timestamp</term><description>Current UTC timestamp</description></item>
    /// <item><term>actorSystem</term><description>"running" or "terminated" based on actor system state</description></item>
    /// </list>
    /// <para>Useful for load balancers, monitoring systems, and diagnostics.</para>
    /// </remarks>
    /// <response code="200">Service is healthy and responding</response>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            actorSystem = actorSystem.WhenTerminated.IsCompleted ? "terminated" : "running"
        });
    }
}