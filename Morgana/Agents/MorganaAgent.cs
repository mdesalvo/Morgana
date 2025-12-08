using Akka.Actor;

namespace Morgana.Agents;

public class MorganaAgent : ReceiveActor
{
    protected readonly string conversationId;
    protected readonly string userId;

    public MorganaAgent(string conversationId, string userId)
    {
        this.conversationId = conversationId;
        this.userId = userId;
    }
}