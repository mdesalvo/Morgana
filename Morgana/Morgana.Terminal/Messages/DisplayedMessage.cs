using Morgana.Contracts;

namespace Morgana.Terminal.Messages;

/// <summary>One entry of the transcript: who said it, the text as it arrived, the colour of its rows and what kind of line it is.</summary>
/// <param name="Who">The speaker the rows are filed under: <c>You</c>, <c>Morgana</c>, an agent or the channel itself for its own notices.</param>
/// <param name="Text">The text as it arrived; the message renderer cleans it for the terminal.</param>
/// <param name="Color">The Spectre colour token of the rows, the speaker's own unless the line is a warning or an error.</param>
/// <param name="MessageType">The wire's <c>MessageType</c> for a delivery, <c>user</c> for the user's own line, <c>system_warning</c> or <c>error</c> for a notice drawn here.</param>
/// <param name="Card">The rich card delivered with the message; null for user echoes and notices as well as for every message of a channel that declares no rich cards.</param>
public sealed record DisplayedMessage(
    string Who,
    string Text,
    string Color,
    string MessageType,
    RichCard? Card = null)
{
    /// <summary>Tells a message of the conversation from a notice, which is what <c>/status</c> counts.</summary>
    public bool IsConversationMessage => MessageType is ChannelMessageTypes.Assistant or ChannelMessageTypes.Presentation or "user";
}
