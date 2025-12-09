using Akka.Actor;
using static Morgana.Records;

namespace Morgana.Agents;

public class ConversationContextAgent : MorganaAgent
{
    private ConversationState? currentState;

    public ConversationContextAgent(string conversationId, string userId)
        : base(conversationId, userId)
    {
        Receive<QueryContextRequest>(HandleQueryContext);
        Receive<UpdateContextRequest>(HandleUpdateContext);
        Receive<ClearContextRequest>(HandleClearContext);
    }

    private void HandleQueryContext(QueryContextRequest msg)
    {
        Sender.Tell(new QueryContextResponse(currentState));
    }

    private void HandleUpdateContext(UpdateContextRequest msg)
    {
        currentState = msg.NewState;
        Sender.Tell(new UpdateContextResponse(true));
    }

    private void HandleClearContext(ClearContextRequest msg)
    {
        currentState = null;
        Sender.Tell(new ClearContextResponse(true));
    }
}