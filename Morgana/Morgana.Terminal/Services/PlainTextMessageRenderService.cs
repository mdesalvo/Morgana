using System.Text;
using System.Text.RegularExpressions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>Draws messages as plain text: no markdown, no cards, only a speaker prefix and rows wrapped by terminal cell.</summary>
public sealed class PlainTextMessageRenderService : IMessageRenderer
{
    /// <summary>What opens the first row of a line said by a speaker of the conversation, ahead of the speaker's name. Drawn only.</summary>
    private const string SpeakerMarker = "● ";

    /// <summary>
    /// Matches a run of three or more consecutive newlines. Morgana formats replies for rich channels with
    /// doubled blank lines that a plain renderer would count as phantom rows, lighting the scroll indicator on a
    /// conversation that fits, so each run collapses to a single blank line as a markdown paragraph break would.
    /// </summary>
    private static readonly Regex BlankRunRegex = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>Wraps every row by terminal cell.</summary>
    private readonly TerminalCellService cells;

    /// <summary>Names the channel, whose own notices are no speaker of the conversation and carry no dot.</summary>
    private readonly ChannelProfile profile;

    /// <summary>Colours the speaker's dot.</summary>
    private readonly TerminalTheme theme;

    /// <summary>Captures the cell measurement, the channel's identity and its theme.</summary>
    public PlainTextMessageRenderService(TerminalCellService cells, ChannelProfile profile, TerminalTheme theme)
    {
        this.cells = cells;
        this.profile = profile;
        this.theme = theme;
    }

    /// <inheritdoc />
    public List<Markup> RenderMessage(DisplayedMessage message, int width)
    {
        List<Markup> rows = [];

        // The text is made terminal-safe before counting, so the rows counted are the rows drawn
        string text = PrepareText(message.Text);

        // The channel's own notices are no speaker of the conversation, so they carry no dot. The dot is
        // wrapped with the row it opens, so the cell budget of that row counts it
        string? speakerMarkerColor = message.Who == profile.ChannelName ? null : theme.SpeakerColor(message.Who);
        string fullText = speakerMarkerColor is null ? $"{message.Who}: {text}" : $"{SpeakerMarker}{message.Who}: {text}";

        // Each line of the text is its own paragraph: a break the author wrote is a break on screen
        bool firstRow = true;
        foreach (string line in fullText.Split('\n'))
        {
            // Wrapped by cell on whole runes, so a wide glyph never overflows the row behind the budget
            foreach (string slice in cells.Wrap(line, width))
            {
                // The slice becomes one row of the message
                rows.Add(RenderRow(message.Color, slice, firstRow, speakerMarkerColor));
                firstRow = false;
            }
        }

        // Rows the UI slices into its scrollback window
        return rows;
    }

    /// <inheritdoc />
    public List<Markup> RenderPartial(string text, string color, int width)
    {
        List<Markup> rows = [];

        // A partial reply wears no prefix: the speaker is named once the reply is committed
        foreach (string line in PrepareText(text).Split('\n'))
            foreach (string slice in cells.Wrap(line, width))
                rows.Add(new Markup($"[{color}]{Markup.Escape(slice)}[/]"));

        // Rows the UI draws under the transcript
        return rows;
    }

    /// <summary>
    /// Makes text published by Morgana safe to draw: control characters out and the margins trimmed. Emoji
    /// shortcodes are resolved next, then the variation selectors they produce are dropped so the width measured is the width drawn.
    /// </summary>
    private string PrepareText(string text) =>
        cells.StripVariationSelectors(BlankRunRegex.Replace(Emoji.Replace(SanitizeMessageText(text).Trim()), "\n\n"));

    /// <summary>Draws one wrapped row: on the first row of a message the speaker's dot and a bold <c>Who:</c> prefix, on every other row the same colour without bold.</summary>
    private static Markup RenderRow(string color, string slice, bool firstRow, string? speakerMarkerColor)
    {
        // Rows after the first carry only the colour
        if (!firstRow)
            return new Markup($"[{color}]{Markup.Escape(slice)}[/]");

        // The dot takes the speaker's own colour even when the row wears a warning's: who spoke and what
        // kind of line it is read apart. It is drawn ahead of the name and is no part of the message
        string marker = string.Empty;
        if (speakerMarkerColor is not null && slice.StartsWith(SpeakerMarker, StringComparison.Ordinal))
        {
            // The dot is drawn in the speaker's colour ahead of the name
            marker = $"[{speakerMarkerColor}]●[/] ";
            slice = slice[SpeakerMarker.Length..];
        }

        // The speaker's name is bold up to its colon and the rest of the row follows in the same colour
        int colonIndex = slice.IndexOf(':');
        if (colonIndex > 0)
            return new Markup($"{marker}[bold {color}]{Markup.Escape(slice[..colonIndex])}:[/][{color}]{Markup.Escape(slice[(colonIndex + 1)..])}[/]");

        // A speaker name that wrapped past the row leaves no colon to split on, so the whole row is the emphasis
        return new Markup($"{marker}[bold {color}]{Markup.Escape(slice)}[/]");
    }

    /// <summary>
    /// Cleans reply text for the terminal while keeping the line structure Morgana authored: newlines survive
    /// since each becomes a row, a tab becomes a space and every other control character is dropped.
    /// </summary>
    private static string SanitizeMessageText(string text)
    {
        StringBuilder plainText = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            // Each char is judged for what it does to the terminal
            char character = text[index];
            switch (character)
            {
                // A reply authored on Windows breaks its lines with a pair: one break, not a break plus a stray glyph
                case '\r' when index + 1 < text.Length && text[index + 1] == '\n':
                    continue;

                // A lone carriage return is a line break on its own
                case '\r':
                // Both break characters become the line break the renderer splits on
                case '\n':
                    plainText.Append('\n');
                    continue;

                // A tab would be drawn at the terminal's own stops and break the cell count
                case '\t':
                    plainText.Append(' ');
                    continue;

                // Any other control character could steer the user's terminal
                default:
                    if (!char.IsControl(character))
                        plainText.Append(character);
                    continue;
            }
        }

        // The text as it may be drawn
        return plainText.ToString();
    }
}
