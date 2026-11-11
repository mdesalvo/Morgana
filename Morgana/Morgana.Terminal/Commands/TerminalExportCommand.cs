using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Morgana.Contracts;
using Morgana.Terminal.Abstractions;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Services;

namespace Morgana.Terminal.Commands;

/// <summary><c>/export path:… [format:…]</c>: writes the conversation as Morgana has it on record to a file on the machine the terminal runs on.</summary>
// The transcript on screen is not the source: what is exported is what survives, so an export taken here matches one taken after a restart.
public sealed class TerminalExportCommand : TerminalCommand
{
    /// <summary>The steps the command reports: reading the conversation from Morgana, then writing the file.</summary>
    private const int ProgressSteps = 2;

    /// <summary>Names the conversation being exported, which <c>/new</c> may have replaced since the palette opened.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Reads the conversation from Morgana.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Builds the command on the session and the REST client.</summary>
    public TerminalExportCommand(TerminalSessionService session, MorganaClientService morganaClientService)
    {
        // The session names the conversation and the client reads it from Morgana.
        this.session = session;
        this.morganaClientService = morganaClientService;
    }

    /// <inheritdoc />
    public override CommandDescriptor Descriptor { get; } = new(
        "export",
        "Write this conversation to a file",
        Options:
        [
            new CommandOption(
                "path",
                "where to write it, quoted when it has spaces",
                Required: true,

                // Spelled as this machine spells one, separators included: the user edits a real path rather
                // than translating an example written for somebody else's operating system
                DefaultValue: DefaultFile()),
            new CommandOption("format", "text for reading, json for tools", DefaultValue: "text", AllowedValues: ["text", "json"])
        ]);

    /// <summary>The file this command writes when nobody names another, on the user's desktop or in their home where there is no desktop.</summary>
    // Its name says what it holds rather than when it was taken, since a default is decided once and would otherwise carry
    // the hour the channel started.
    private static string DefaultFile()
    {
        // The desktop is where a person looks for a file they asked for.
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // A machine with no desktop folder gets the file in the user's home.
        if (string.IsNullOrEmpty(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // The default file lives in the folder found above
        return Path.Combine(folder, "morgana-conversation.txt");
    }

    /// <summary>True, because a spent conversation is still worth keeping, which is when an export is most likely wanted.</summary>
    public override bool AvailableWhenSpent => true;

    /// <inheritdoc />
    public override async Task ExecuteAsync(ITerminalUi ui, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The value is one of the declared formats by now, spelled however the user typed it
        string format = options["format"].Trim().ToLowerInvariant();

        // A long conversation takes a moment to come back over the wire, which is the first thing the user waits on
        ui.ShowProgress(new CommandProgress(Descriptor.Name, "reading the conversation", Completed: 0, Total: ProgressSteps));

        // The history comes from Morgana's record, so it holds what was said even before this process started
        IReadOnlyList<MorganaChatMessage> messages = await morganaClientService.GetHistoryAsync(session.ConversationId, cancellationToken);

        // Nothing on record yet, so nothing is written.
        if (messages.Count == 0)
        {
            // Writing an empty file over an existing export would lose the earlier one for nothing
            ui.ShowCommandOutcome("found nothing on record for this conversation yet");
            return;
        }

        // A path this machine cannot accept is the user's to fix, so it is said before the work is done
        if (DescribePathProblem(options["path"], out string path) is { } pathProblem)
        {
            // The path problem is said as the reason the export did not happen
            ui.ShowCommandOutcome($"cannot write there: {pathProblem}", isFailure: true);
            return;
        }

        // The file is named in the frame: on a slow disk it is what the user is waiting for
        ui.ShowProgress(new CommandProgress(Descriptor.Name, $"writing {Path.GetFileName(path)}", Completed: 1, Total: ProgressSteps));

        // The format picked decides the shape of the file
        string content = format == "json" ? RenderAsJson(messages) : RenderAsText(messages);

        try
        {
            // The folder of the file is created when it does not exist yet
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            // The transcript replaces whatever file stood at that path.
            await File.WriteAllTextAsync(path, content, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A path the machine refuses is the user's to fix, so it is reported as it was given
            ui.ShowCommandOutcome($"could not write {path}: {ex.Message}", isFailure: true);
            return;
        }

        // The outcome tells how much was written and where
        ui.ShowCommandOutcome($"wrote {messages.Count} message{(messages.Count == 1 ? string.Empty : "s")} to {path}");
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
        // The transcript is built line by line
        StringBuilder transcript = new();

        // A header names the conversation and when it was exported.
        transcript.AppendLine(CultureInfo.InvariantCulture, $"# Conversation {messages[0].ConversationId}");
        transcript.AppendLine(CultureInfo.InvariantCulture, $"# Exported {DateTime.Now:yyyy-MM-dd HH:mm}");
        transcript.AppendLine();

        // Each message becomes its own block of the file
        foreach (MorganaChatMessage message in messages)
        {
            // The header of a block says when and by whom it was said
            transcript.AppendLine(CultureInfo.InvariantCulture, $"[{message.Timestamp:yyyy-MM-dd HH:mm:ss}] {message.AgentName}");
            transcript.AppendLine(message.Text);

            // The options offered at that point are part of what was said: without them a branch in the
            // conversation reads as the user answering a question nobody asked
            if (message.QuickReplies is { Count: > 0 } quickReplies)
                transcript.AppendLine(CultureInfo.InvariantCulture, $"    ({string.Join(" | ", quickReplies.Select(reply => reply.Label))})");

            // A blank line closes the block.
            transcript.AppendLine();
        }

        // The text of the whole file
        return transcript.ToString();
    }

    /// <summary>What is wrong with <paramref name="typedPath"/> as a file to write on this machine; null when nothing is and <paramref name="path"/> holds it as the file system spells it.</summary>
    private static string? DescribePathProblem(string typedPath, out string path)
    {
        // No path is handed back until the typed one is proven writable.
        path = string.Empty;

        // A leading ~ is the user's own way of naming their home, which no file API resolves for them. Both
        // separators are accepted after it, since a path is often carried over from another machine
        string candidatePath = typedPath.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), typedPath[1..].TrimStart('/', '\\'))
            : typedPath;

        // What one operating system allows in a name another refuses, so the refusal comes from this one
        if (candidatePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return "the name carries characters this system does not allow in a path";

        try
        {
            // A relative path is anchored to the terminal's working directory here, which is also where a malformed one is caught
            path = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The message of the framework says what is wrong with the path
            return ex.Message;
        }

        // A directory named as the target would be written over as though it were a file
        if (Directory.Exists(path))
            return $"{path} is a folder: name the file to write inside it";

        // A name this system reads as a folder cannot hold the transcript either
        if (Path.GetFileName(path).Length == 0)
            return $"{path} names no file";

        // Nothing is wrong with the path
        return null;
    }
}