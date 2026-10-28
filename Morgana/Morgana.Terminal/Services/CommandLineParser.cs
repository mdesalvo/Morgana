using System.Text;

namespace Morgana.Terminal.Services;

/// <summary>
/// Reads what the user typed after the slash: the command name, then its <c>name:value</c> options. Every
/// surface asking what a line means comes here, so the palette matching a name, the question naming what is
/// about to run and the command finally receiving its values all read the same line the same way.
/// </summary>
public static class CommandLineParser
{
    /// <summary>The name typed after the slash, without its options; empty right after a bare slash.</summary>
    public static string ParseName(string input)
    {
        // A bare slash names nothing yet, which the palette reads as every command matching
        string line = input.StartsWith('/') ? input[1..] : input;
        int nameEnd = line.IndexOf(' ');
        return (nameEnd < 0 ? line : line[..nameEnd]).Trim();
    }

    /// <summary>
    /// The options written after the name, keyed by name and never null. A value may be quoted, which is how a
    /// path with spaces is written. Anything that is not <c>name:value</c> leaves <paramref name="problem"/>
    /// with the line to show the user; nothing is run on a line that could not be read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseOptions(string input, out string? problem)
    {
        problem = null;
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);

        // The name and the options share one line: the name ends at the first space
        string line = input.StartsWith('/') ? input[1..] : input;
        int nameEnd = line.IndexOf(' ');
        if (nameEnd < 0)
            return options;

        // Each token is one option, with quoted values kept whole
        foreach (string token in SplitRespectingQuotes(line[(nameEnd + 1)..]))
        {
            // Only the first colon separates: a value is free to carry its own, as a Windows path or a URL does
            int separator = token.IndexOf(':');
            if (separator <= 0)
            {
                // Typically the tail of a value with spaces in it: a path left unquoted arrives here as its own
                // token; truncating the value silently would write the file somewhere else
                problem = $"'{token}' is not an option: write name:value, with quotes around a value that has spaces";
                return options;
            }

            // The text before the first colon names the option
            string name = token[..separator];
            string value = Unquote(token[(separator + 1)..]);
            if (value.Length == 0)
            {
                // An option with nothing after its colon is refused
                problem = $"option '{name}' was given no value";
                return options;
            }
            // A second value for the same option is refused
            if (!options.TryAdd(name, value))
            {
                // Two values for one option leave no way to tell which the user meant
                problem = $"option '{name}' is given twice";
                return options;
            }
        }

        // Every token was read: the options as written
        return options;
    }

    /// <summary>The tokens of <paramref name="text"/>, keeping what a pair of quotes holds together.</summary>
    private static List<string> SplitRespectingQuotes(string text)
    {
        List<string> tokens = [];
        StringBuilder token = new();
        bool insideQuotes = false;

        // The line is read one character at a time to know whether a space is inside quotes
        foreach (char character in text)
        {
            // A quote opens or closes a quoted stretch
            if (character == '"')
            {
                // The quotes travel with the token and are dropped once the value is cut out of it
                insideQuotes = !insideQuotes;
                token.Append(character);
            }
            else if (character == ' ' && !insideQuotes)
            {
                // A space outside quotes ends the token, unless nothing was written since the last one
                if (token.Length > 0)
                    tokens.Add(token.ToString());
                token.Clear();
            }
            else
                token.Append(character);
        }

        // The text after the last space is the last token
        if (token.Length > 0)
            tokens.Add(token.ToString());
        return tokens;
    }

    /// <summary>The value a pair of quotes holds, or the text itself when it carries none.</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"') ? value[1..^1] : value;
}
