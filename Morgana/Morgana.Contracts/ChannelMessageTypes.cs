namespace Morgana.Contracts;

/// <summary>
/// What kind of thing an outbound message is, declared on <see cref="ChannelMessage.MessageType"/> and
/// read by every channel to decide how to paint it. Two of them are conversation (somebody
/// said something to somebody); the rest are notices about the conversation rather than
/// part of it: a channel shows those as banners that fade. Morgana keeps none of them on
/// record, because a transcript is what was said. The wire carries a string, so a channel built
/// before a new type exists still reads the message and paints it as it would an unknown one.
/// </summary>
public static class ChannelMessageTypes
{
    /// <summary>An answer, from an agent or from Morgana herself.</summary>
    public const string Assistant = "assistant";

    /// <summary>Morgana opening a conversation or handing one back, styled apart from an answer.</summary>
    public const string Presentation = "presentation";

    /// <summary>A notice about the conversation carrying no reply, such as a budget running low.</summary>
    public const string SystemWarning = "system_warning";

    /// <summary>
    /// A command's own frame or outcome, never anything else: a channel keeps it out of the transcript
    /// on this type alone, since a command is not a turn of the conversation.
    /// </summary>
    public const string System = "system";

    /// <summary>A notice that something stopped the turn, such as a budget that ran out.</summary>
    public const string Error = "error";
}