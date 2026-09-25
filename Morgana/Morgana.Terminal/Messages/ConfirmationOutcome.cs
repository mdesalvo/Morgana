namespace Morgana.Terminal.Messages;

/// <summary>What a keystroke did to the confirmation question a command asked before it may run.</summary>
public enum ConfirmationOutcome
{
    /// <summary>The question is still on screen: the keystroke moved the highlight or meant nothing here.</summary>
    Pending,

    /// <summary>The user answered Yes: the command the question was asked for may run now.</summary>
    Confirmed,

    /// <summary>The user answered No or dismissed the question: the command is abandoned and nothing ran.</summary>
    Declined
}