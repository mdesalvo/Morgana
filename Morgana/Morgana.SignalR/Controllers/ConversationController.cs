using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.Framework;
using Morgana.Framework.Actors;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;
using Morgana.SignalR.Hubs;

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
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// Initializes a new instance of the ConversationController.
    /// </summary>
    /// <param name="actorSystem">Akka.NET actor system for conversation management</param>
    /// <param name="logger">Logger instance for diagnostic information</param>
    /// <param name="conversationPersistenceService">Service for recovering an existing conversation</param>
    public ConversationController(
        ActorSystem actorSystem,
        ILogger logger,
        IConversationPersistenceService conversationPersistenceService)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.conversationPersistenceService = conversationPersistenceService;
    }

    /// <summary>
    /// Starts a new conversation by creating a ConversationManagerActor and triggering presentation generation.
    /// </summary>
    /// <param name="request">Request containing the conversation ID to start</param>
    /// <returns>
    /// 202 Accepted with conversation details immediately.
    /// 500 Internal Server Error on failure.
    /// </returns>
    /// <remarks>
    /// <para>This endpoint:</para>
    /// <list type="number">
    /// <item>Creates or retrieves the ConversationManagerActor for the given conversation ID</item>
    /// <item>Sends CreateConversation message via Tell (fire-and-forget, no temporary actors)</item>
    /// <item>Returns immediately - the manager creates supervisor asynchronously</item>
    /// <item>The supervisor automatically generates and sends a presentation message via SignalR</item>
    /// </list>
    /// <para><strong>Client Flow:</strong> Call this endpoint, then listen on SignalR for the presentation message.</para>
    /// <para><strong>Note:</strong> HTTP 202 Accepted indicates the conversation creation was queued. 
    /// The actual creation happens asynchronously. The client should wait for the presentation message via SignalR 
    /// to confirm the conversation is ready.</para>
    /// </remarks>
    /// <response code="202">Conversation creation queued successfully</response>
    /// <response code="500">Internal error occurred</response>
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
    /// Resumes an existing conversation by restoring actor hierarchy and active agent state from persistent storage.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to resume</param>
    /// <returns>
    /// 202 Accepted with conversation details and restored active agent immediately.<br/>
    /// 500 Internal Server Error on failure.
    /// </returns>
    /// <remarks>
    /// <para>This endpoint:</para>
    /// <list type="number">
    /// <item>Creates or retrieves the ConversationManagerActor and supervisor</item>
    /// <item>Sends CreateConversation message via Tell (fire-and-forget, no temporary actors)</item>
    /// <item>Retrieves last active agent from database</item>
    /// <item>Sends RestoreActiveAgent message to supervisor via Tell</item>
    /// <item>Returns immediately - restoration happens asynchronously</item>
    /// </list>
    /// <para><strong>Client Flow:</strong> After calling this endpoint, the client should fetch conversation history 
    /// via GET /api/conversation/{id}/history to populate the UI.</para>
    /// </remarks>
    /// <response code="202">Conversation resume queued successfully</response>
    /// <response code="500">Internal error occurred</response>
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
    /// 200 OK with array of MorganaChatMessage on success.<br/>
    /// 404 Not Found if conversation doesn't exist.<br/>
    /// 500 Internal Server Error on failure.
    /// </returns>
    /// <remarks>
    /// <para><strong>Use Case:</strong></para>
    /// <para>Called by Cauldron frontend after resuming a conversation to display complete message history.
    /// This is a synchronous HTTP call, not via SignalR, to ensure the history is loaded before the UI
    /// removes the magical loader.</para>
    /// <para><strong>Data Flow:</strong></para>
    /// <code>
    /// 1. Client resumes conversation (POST /api/conversation/{id}/resume)
    /// 2. Client joins SignalR group (await SignalRService.JoinConversation)
    /// 3. Client calls this endpoint (GET /api/conversation/{id}/history)
    /// 4. Backend loads from SQLite, decrypts, deserializes, maps to MorganaChatMessage[]
    /// 5. Client populates messages[] array and removes loader
    /// </code>
    /// </remarks>
    /// <response code="200">History retrieved successfully</response>
    /// <response code="404">Conversation not found</response>
    /// <response code="500">Internal error occurred</response>
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
}