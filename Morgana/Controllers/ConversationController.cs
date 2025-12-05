using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Morgana.Messages;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger<ConversationController> logger;

    public ConversationController(ActorSystem actorSystem, ILogger<ConversationController> logger)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
    }

    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] UserMessageRequest request)
    {
        ActorSelection? conversationSupervisor = actorSystem.ActorSelection("/user/conversation-supervisor");
        ConversationResponse? conversationResponse = await conversationSupervisor.Ask<ConversationResponse>(
            new UserMessage(request.UserId, request.SessionId, request.Message), TimeSpan.FromSeconds(30));

        return Ok(conversationResponse);
    }
}