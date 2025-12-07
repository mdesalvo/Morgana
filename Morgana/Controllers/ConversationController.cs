using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.Hubs;
using Morgana.Messages;
using Morgana.Interfaces;
using System.Collections.Concurrent;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem _actorSystem;
    private readonly ILogger<ConversationController> _logger;
    private readonly IHubContext<ConversationHub> _hubContext;
    private readonly IStorageService _storageService;
    
    // Cache locale dei supervisor refs
    private static readonly ConcurrentDictionary<string, IActorRef> _supervisors = new();
    
    public ConversationController(
        ActorSystem actorSystem,
        ILogger<ConversationController> logger,
        IHubContext<ConversationHub> hubContext,
        IStorageService storageService)
    {
        _actorSystem = actorSystem;
        _logger = logger;
        _hubContext = hubContext;
        _storageService = storageService;
    }
    
    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        try
        {
            string conversationId = Guid.NewGuid().ToString();
            
            // Get o crea supervisor
            IActorRef supervisor = _supervisors.GetOrAdd(
                "main-supervisor",
                _ => _actorSystem.ActorSelection("/user/supervisor")
                    .ResolveOne(TimeSpan.FromSeconds(5)).Result
            );
            
            // Crea conversation
            ConversationCreated? response = await supervisor.Ask<ConversationCreated>(
                new CreateConversation(conversationId, request.UserId),
                TimeSpan.FromSeconds(10)
            );
            
            _logger.LogInformation($"Started conversation {conversationId} for user {request.UserId}");
            
            return Ok(new
            {
                conversationId = response.ConversationId,
                message = "Conversation started successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start conversation");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("{conversationId}/message")]
    public async Task<IActionResult> SendMessage(string conversationId, [FromBody] SendMessageRequest request)
    {
        try
        {
            if (!_supervisors.ContainsKey("main-supervisor"))
            {
                return BadRequest(new { error = "No active supervisor" });
            }

            IActorRef supervisor = _supervisors["main-supervisor"];
            
            // Invia messaggio all'actor system
            UserMessage userMessage = new UserMessage(
                conversationId,
                request.UserId,
                request.Text,
                DateTime.UtcNow
            );
            
            // Ottieni conversation manager
            ActorSelection? conversationManager = _actorSystem.ActorSelection(
                $"/user/supervisor/conversation-{conversationId}"
            );
            
            // Invia messaggio (fire and forget, risposta via SignalR)
            conversationManager.Tell(userMessage);
            
            _logger.LogInformation($"Message sent to conversation {conversationId}");
            
            return Accepted(new
            {
                conversationId,
                message = "Message processing started",
                note = "Response will be sent via SignalR"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send message to conversation {conversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
    
    [HttpPost("{conversationId}/end")]
    public IActionResult EndConversation(string conversationId)
    {
        try
        {
            if (!_supervisors.ContainsKey("main-supervisor"))
            {
                return BadRequest(new { error = "No active supervisor" });
            }

            IActorRef supervisor = _supervisors["main-supervisor"];
            supervisor.Tell(new TerminateConversation(conversationId));
            
            _logger.LogInformation($"Ended conversation {conversationId}");
            
            return Ok(new { message = "Conversation ended" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to end conversation {conversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new 
        { 
            status = "healthy",
            timestamp = DateTime.UtcNow,
            actorSystem = _actorSystem.WhenTerminated.IsCompleted ? "terminated" : "running"
        });
    }
}