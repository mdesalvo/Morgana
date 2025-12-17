using Akka.Actor;
using Akka.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.Actors;
using Morgana.Hubs;
using static Morgana.Records;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger<ConversationController> logger;
    private readonly IHubContext<ConversationHub> signalrContext;

    public ConversationController(
        ActorSystem actorSystem,
        ILogger<ConversationController> logger,
        IHubContext<ConversationHub> signalrContext)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.signalrContext = signalrContext;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        try
        {
            logger.LogInformation($"Starting conversation {request.ConversationId}");

            IActorRef manager = await GetOrCreateManager(request.ConversationId);

            ConversationCreated? conversationCreated = await manager.Ask<ConversationCreated>(
                new CreateConversation(request.ConversationId));

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

    [HttpPost("{conversationId}/end")]
    public async Task<IActionResult> EndConversation(string conversationId)
    {
        try
        {
            logger.LogInformation($"Ending conversation {conversationId}");

            IActorRef manager = await GetOrCreateManager(conversationId);

            manager.Tell(new TerminateConversation(conversationId));

            logger.LogInformation($"Ended conversation {conversationId}");

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
            logger.LogInformation($"Sending message conversation {request.ConversationId}");

            IActorRef manager = await GetOrCreateManager(request.ConversationId);

            manager.Tell(new UserMessage(
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

    private async Task<IActorRef> GetOrCreateManager(string conversationId)
    {
        string managerName = $"manager-{conversationId}";

        try
        {
            // se esiste, lo recuperiamo
            return await actorSystem.ActorSelection($"/user/{managerName}")
                                    .ResolveOne(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // altrimenti lo creiamo
            Props props = DependencyResolver.For(actorSystem)
                .Props<ConversationManagerActor>(conversationId);

            return actorSystem.ActorOf(props, managerName);
        }
    }
}