using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Interfaces;

/// <summary>
/// How a channel draws what is said in the conversation. Every <see cref="Markup"/> returned is exactly one
/// terminal row no wider than the width asked: the UI budgets the transcript against that count.
/// </summary>
public interface IMessageRenderer
{
    /// <summary>Draws <paramref name="message"/> at <paramref name="width"/> cells: the speaker's prefix on the first row then the text, with the card beneath where the channel renders one.</summary>
    List<Markup> RenderMessage(DisplayedMessage message, int width);

    /// <summary>Draws the part of a reply that has been revealed so far, in <paramref name="color"/> and without a speaker prefix, since the reply it belongs to is not committed yet.</summary>
    List<Markup> RenderPartial(string text, string color, int width);
}
