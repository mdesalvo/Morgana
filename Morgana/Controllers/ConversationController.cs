using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Morgana.Messages;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem _actorSystem;
    private readonly ILogger<ConversationController> _logger;

    public ConversationController(ActorSystem actorSystem, ILogger<ConversationController> logger)
    {
        _actorSystem = actorSystem;
        _logger = logger;
    }

    [HttpPost("message")]
    public async Task<IActionResult> SendMessage([FromBody] UserMessageRequest request)
    {
        var supervisor = _actorSystem.ActorSelection("/user/conversation-supervisor");
        var response = await supervisor.Ask<ConversationResponse>(
            new UserMessage(request.UserId, request.SessionId, request.Message),
            TimeSpan.FromSeconds(30));

        return Ok(response);
    }
}