using Akka.Actor;

namespace Morgana.AI.Actors;

public class MorganaActor : ReceiveActor
{
    protected readonly string conversationId;

    public MorganaActor(string conversationId)
    {
        this.conversationId = conversationId;
    }
}