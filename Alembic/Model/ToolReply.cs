using System.ComponentModel;

namespace Alembic.Model;

/// <summary>
/// What a tool of Alembic's own answers to the model: the sentence it acts on in the same turn, or the reason that the call changed nothing.
/// </summary>
public sealed record ToolReply(
    [Description("What the call recorded or returns, in the words the model acts on next. Empty when the call recorded or changed nothing.")] string Text,
    [Description("Why the call recorded or changed nothing. Null whenever the call recorded or returned something.")] string? Error = null)
{
    /// <summary>Answers a call that recorded or changed nothing, the reason standing in the failure field and the text left empty.</summary>
    /// <param name="error">Why nothing was recorded or changed.</param>
    public static ToolReply Refused(string error) => new(string.Empty, error);
}
