using Akka.Actor;

namespace Morgana.AI.Abstractions;

public class MorganaActor : ReceiveActor
{
    protected readonly string conversationId;

    protected MorganaActor(string conversationId)
    {
        this.conversationId = conversationId;
    }
}