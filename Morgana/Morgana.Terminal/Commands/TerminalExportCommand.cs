using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Services;

namespace Morgana.Terminal.Commands;

/// <summary>
/// <c>/export path:… [format:…]</c>: writes the conversation as Morgana has it on record to a file on the
/// machine the terminal runs on. The transcript on screen is not the source: what is exported is what
/// survives, so an export taken here matches one taken after a restart.
/// </summary>
public sealed class TerminalExportCommand : TerminalCommand
{
    /// <summary>Names the conversation being exported, which <c>/new</c> may have replaced since the palette opened.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Reads the conversation from Morgana.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Captures the session and the REST client.</summary>
    public TerminalExportCommand(TerminalSessionService session, MorganaClientService morganaClientService)
    {
        this.session = session;
        this.morganaClientService = morganaClientService;
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; } = new(
        "export",
        "Write this conversation to a file",
        Options:
        [
            new CommandOption("path", "where to write it, quoted when it has spaces", Required: true),
            new CommandOption("format", "text or json; text when not said")
        ]);

    /// <summary>A spent conversation is still worth keeping, which is when an export is most likely wanted.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override async Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        string format = options.GetValueOrDefault("format", "text").Trim().ToLowerInvariant();
        if (format is not ("text" or "json"))
        {
            ui.ShowNotice($"/export cannot write '{format}': the formats are text and json", isFailure: true);
            return;
        }

        IReadOnlyList<MorganaChatMessage> messages = await morganaClientService.GetHistoryAsync(session.ConversationId, cancellationToken);
        if (messages.Count == 0)
        {
            // Writing an empty file over an existing export would lose the earlier one for nothing
            ui.ShowNotice("/export found nothing on record for this conversation yet");
            return;
        }

        // A leading ~ is the user's own way of naming their home, which no file API resolves for them
        string path = ResolvePath(options["path"]);
        string content = format == "json" ? RenderAsJson(messages) : RenderAsText(messages);

        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A path the machine refuses is the user's to fix, so it is reported as it was given
            ui.ShowNotice($"/export could not write {path}: {ex.Message}", isFailure: true);
            return;
        }

        ui.ShowNotice($"/export wrote {messages.Count} message{(messages.Count == 1 ? string.Empty : "s")} to {path}");
    }

    /// <summary>The conversation with the field names the REST API uses, so an export reads back as what Morgana serves.</summary>
    private static string RenderAsJson(IReadOnlyList<MorganaChatMessage> messages) =>
        // Apostrophes and accents are left as themselves: the file is read by people and by tools, never by a browser
        JsonSerializer.Serialize(messages, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    /// <summary>The conversation as a file meant to be read by a person: who spoke, when, what was said.</summary>
    private static string RenderAsText(IReadOnlyList<MorganaChatMessage> messages)
    {
        StringBuilder transcript = new();
        transcript.AppendLine($"# Conversation {messages[0].ConversationId}");
        transcript.AppendLine($"# Exported {DateTime.Now:yyyy-MM-dd HH:mm}");
        transcript.AppendLine();

        foreach (MorganaChatMessage message in messages)
        {
            transcript.AppendLine($"[{message.Timestamp:yyyy-MM-dd HH:mm:ss}] {message.AgentName}");
            transcript.AppendLine(message.Text);

            // The options offered at that point are part of what was said: without them a branch in the
            // conversation reads as the user answering a question nobody asked
            if (message.QuickReplies is { Count: > 0 } quickReplies)
                transcript.AppendLine($"    ({string.Join(" | ", quickReplies.Select(reply => reply.Label))})");
            transcript.AppendLine();
        }

        return transcript.ToString();
    }

    /// <summary>The path as the file system understands it, with the home shorthand spelled out.</summary>
    private static string ResolvePath(string path)
    {
        string expanded = path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'))
            : path;

        // The terminal's working directory is not where the user is looking, so a relative path is anchored to it here
        return Path.GetFullPath(expanded);
    }
}
