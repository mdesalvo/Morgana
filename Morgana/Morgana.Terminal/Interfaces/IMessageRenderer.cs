using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Interfaces;

/// <summary>
/// How a channel draws what is said in the conversation, one returned <see cref="Markup"/> for each terminal row.
/// Every row is no wider than the width asked, because the UI budgets the transcript against the count of rows.
/// </summary>
public interface IMessageRenderer
{
    /// <summary>Draws <paramref name="message"/> at <paramref name="width"/> cells: the speaker's prefix on the first row, then the text, with the card beneath where the channel renders one.</summary>
    List<Markup> RenderMessage(DisplayedMessage message, int width);

    /// <summary>Draws the part of a reply revealed so far, in <paramref name="color"/> and without a speaker prefix, since the reply is not committed yet.</summary>
    List<Markup> RenderPartial(string text, string color, int width);
}