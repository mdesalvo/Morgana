namespace Morgana.Terminal.Messages;

/// <summary>
/// What the conversation on screen is, as far as deciding which commands may be offered goes: the palette
/// lists a command only where running it would mean something.
/// </summary>
/// <param name="Spent">True once the dust budget is exhausted, when Morgana refuses to work on this conversation.</param>
/// <param name="AgentCarriesConversation">True while an agent is carrying the conversation, which is what an agent-scoped command acts on.</param>
public readonly record struct TerminalConversationState(
    bool Spent,
    bool AgentCarriesConversation);
