using Morgana.Terminal;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Spectre.Console;

namespace Grimoire.Services;

/// <summary>Draws messages the way a rich terminal can: prose through markdown, with the rich card Spectrized beneath it.</summary>
public sealed class MarkdownMessageRenderService : IMessageRenderer
{
    /// <summary>Renders prose, with the speaker's prefix on the first row, to rows measured by terminal cell.</summary>
    private readonly MarkdownTerminalRenderService markdownRenderer;

    /// <summary>Renders a rich card to a bordered box beneath the prose.</summary>
    private readonly RichCardTerminalRenderService richCardRenderer;

    /// <summary>Names the channel, whose own notices are no speaker of the conversation and carry no dot.</summary>
    private readonly ChannelProfile profile;

    /// <summary>Colours the speaker's dot.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Captures the two renderers it composes, the channel's identity and its theme.</summary>
    public MarkdownMessageRenderService(
        MarkdownTerminalRenderService markdownRenderer,
        RichCardTerminalRenderService richCardRenderer,
        ChannelProfile profile,
        TerminalTheme theme)
    {
        // The renderers the message is composed from
        this.markdownRenderer = markdownRenderer;
        this.richCardRenderer = richCardRenderer;
        this.profile = profile;
        this.theme = theme;
    }

    /// <inheritdoc />
    public List<Markup> RenderMessage(DisplayedMessage message, int width)
    {
        // The channel's own notices are no speaker of the conversation, so they carry no dot
        string? speakerMarkerColor = message.Who == profile.ChannelName ? null : theme.SpeakerColor(message.Who);
        List<Markup> rows = markdownRenderer.RenderToRows(message.Text, message.Color, $"{message.Who}: ", width, speakerMarkerColor);

        // A card is stacked under the text after one blank row, mirroring Cauldron's text bubble then card
        if (message.Card is not null)
        {
            // The blank row separates the text from the card
            rows.Add(new Markup(string.Empty));
            rows.AddRange(richCardRenderer.RenderRichCard(message.Card, message.Color, width));
        }

        // Rows the UI slices into its scrollback window
        return rows;
    }

    /// <inheritdoc />
    public List<Markup> RenderPartial(string text, string color, int width)
    {
        // The markdown is parsed again at every reveal, so syntax resolves as its closing tokens arrive and the
        // reply does not jump from raw to formatted when it is committed. No prefix: the speaker is named once committed
        return markdownRenderer.RenderToRows(text, color, null, width);
    }
}
