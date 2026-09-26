namespace Morgana.Terminal.Messages;

/// <summary>The conversation on screen as the header shows it, read in one moment so <c>/status</c> never disagrees with it.</summary>
/// <param name="Speaker">Who holds the conversation, spelled as the header spells it: <c>Morgana</c> or <c>Morgana (Billing)</c>.</param>
/// <param name="DustLevel">Remaining dust as a fraction of the budget, 1.0 full; null until Morgana reports one or when it limits none.</param>
/// <param name="Spent">True once the budget is exhausted and Morgana refuses to work on this conversation.</param>
/// <param name="MessageCount">The messages the user has seen exchanged, their own included; notices are not messages.</param>
public readonly record struct TerminalSessionStatus(
    string Speaker,
    double? DustLevel,
    bool Spent,
    int MessageCount);
