using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.Hubs;
using Akka.DependencyInjection;
using Morgana.Agents;
using static Morgana.Records;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger<ConversationController> logger;
    private readonly IHubContext<ConversationHub> hubContext;

    public ConversationController(
        ActorSystem actorSystem,
        ILogger<ConversationController> logger,
        IHubContext<ConversationHub> hubContext)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.hubContext = hubContext;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        try
        {
            logger.LogInformation($"Starting conversation {request.ConversationId} for user {request.UserId}");

            IActorRef manager = await GetOrCreateManager(request.ConversationId, request.UserId);

            ConversationCreated? conversationCreated = await manager.Ask<ConversationCreated>(
                new CreateConversation(request.ConversationId, request.UserId));

            logger.LogInformation($"Started conversation {conversationCreated.ConversationId} for user {conversationCreated.UserId}");

            return Ok(new
            {
                conversationId = conversationCreated.ConversationId,
                userId = conversationCreated.UserId,
                message = "Conversation started successfully"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start conversation");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{conversationId}/end")]
    public async Task<IActionResult> EndConversation(string conversationId, string userId)
    {
        try
        {
            logger.LogInformation($"Ending conversation {conversationId} for user {userId}");

            IActorRef manager = await GetOrCreateManager(conversationId, userId);

            manager.Tell(new TerminateConversation(conversationId, userId));

            logger.LogInformation($"Ended conversation {conversationId} for user {userId}");

            return Ok(new { message = "Conversation ended" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to end conversation {conversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            logger.LogInformation($"Sending message conversation {request.ConversationId} to user {request.UserId}");

            IActorRef manager = await GetOrCreateManager(request.ConversationId, request.UserId);

            manager.Tell(new UserMessage(
                request.ConversationId,
                request.UserId,
                request.Text,
                DateTime.UtcNow
            ));

            logger.LogInformation($"Message sent to conversation {request.ConversationId} to user {request.UserId}");

            return Accepted(new
            {
                conversationId = request.ConversationId,
                userId = request.UserId,
                message = "Message processing started",
                note = "Response will be sent via SignalR"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to send message to conversation {request.ConversationId} to user {request.UserId}");
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
            actorSystem = actorSystem.WhenTerminated.IsCompleted ? "terminated" : "running"
        });
    }
    
    private async Task<IActorRef> GetOrCreateManager(string conversationId, string userId)
    {
        string managerName = $"manager-{conversationId}";
        string path = $"/user/{managerName}";

        try
        {
            // se esiste, lo recuperiamo
            return await actorSystem.ActorSelection(path).ResolveOne(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // altrimenti lo creiamo
            Props props = DependencyResolver.For(actorSystem)
                .Props<ConversationManagerAgent>(conversationId, userId);

            return actorSystem.ActorOf(props, managerName);
        }
    }
}