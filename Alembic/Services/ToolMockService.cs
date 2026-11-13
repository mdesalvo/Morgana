using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Alembic.Interfaces;
using Alembic.Models;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IToolMockService"/>: one completion per agent, on the Efficiency tier.
/// Nothing here is a conversation, one toolkit in and one file out, so a plain completion serves where the interview needs tools to hold its state across turns.
/// </summary>
public class ToolMockService : IToolMockService
{
    /// <summary>The prompt in <c>alembic.json</c> that governs mock authoring.</summary>
    private const string MockPromptId = "CodeMocker";

    /// <summary>Resolves the mock author's prompt.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the generated half whose signatures the mock implements.</summary>
    private readonly ICodeEmitService codeEmitService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records a generation that was resumed or went silent.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the mock author to its prompt, the generated half and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>CodeMocker</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="codeEmitService">Supplies the generated tool signatures the mock must implement.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records a generation that was resumed or went silent.</param>
    public ToolMockService(
        IAlembicPromptService alembicPromptService,
        ICodeEmitService codeEmitService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.codeEmitService = codeEmitService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>
    /// Writes the client-owned tool class for one agent, throwing <see cref="InvalidOperationException"/> when the model writes no source or insists on a constructor.
    /// A failure thrown here becomes a file in the archive saying what went wrong, which is worth more than an empty or unbuildable source.
    /// </summary>
    /// <param name="agent">The agent whose toolkit needs a mock body, read for its Target, Formatting and tools.</param>
    /// <param name="intentName">The intent this agent answers, which regenerates the very signatures the caller already has.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>The whole <c>Tools/*.cs</c> source file, fence-stripped and ready to write to the archive.</returns>
    public async Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default)
    {
        // The mock author's own prose: how to write a mock and what a good one returns.
        Records.Prompt mock = alembicPromptService.Resolve(MockPromptId);

        // Composed without Morgana's layer, unlike every interview pass. Her Personality is how she
        // speaks to someone; this call speaks to nobody and emits a file. Handing it a voice would
        // be the same defect the interview doctrine warns about, pointed the other way.
        string system = string.Join("\n\n",
            new[]
            {
                Records.Prompt.Labeled(Constants.SectionLabels.Target, mock.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, mock.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, mock.Formatting)
            }.Where(section => section.Length > 0));

        // The generated half is the specification: it carries the exact signatures the answer must
        // implement, the class name, the namespace and the base class. Describing them in prose as
        // well would be a second, drifting statement of something already exact. The packager only
        // asks for a mock of an agent with tools, so the emit always holds a tool class.
        EmittedFile signatures = codeEmitService.Emit(agent, intentName)
            .First(emittedFile => emittedFile.Path.Contains("/Tools/", StringComparison.Ordinal) || emittedFile.Path.StartsWith("Tools/", StringComparison.Ordinal));

        // The request: the generated half to complete, what each argument carries and what the agent is for.
        StringBuilder request = new StringBuilder();
        request.AppendLine("This is the generated half of the tool class. Write the other half.");
        request.AppendLine();
        request.AppendLine(signatures.Content);
        AppendArgumentContract(request, agent);
        request.AppendLine();
        request.AppendLine(CultureInfo.InvariantCulture, $"The agent this toolkit belongs to exists for: {agent.Target}");

        // How the agent presents the results decides what shape of data makes a convincing mock.
        if (!string.IsNullOrWhiteSpace(agent.Formatting))
        {
            request.AppendLine();
            request.AppendLine(CultureInfo.InvariantCulture, $"It presents what these tools return like this, so return data that makes it possible: {agent.Formatting}");
        }

        // The class the two halves share, named by the fact the emit already carries rather than
        // guessed: what the generated half declares is what a second declaration would collide with.
        string className = agent.Code.ToolClassName ?? intentName;

        // Plausible domain data written as compiling C# is a capable model's job, the tier every Alembic pass runs on.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        // A whole source file, resumed past the provider's limit until the model finishes it.
        string authored = await StreamedCompletion.RunAsync(
            chatClient, system, request.ToString(),
            length => logger.LogInformation(
                "The mock for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                agent.ID, length),
            length => logger.LogWarning(
                "The mock for {AgentId} went silent after {Length} characters; retrying once",
                agent.ID, length),
            cancellationToken);

        // Thrown rather than returned, so the packager's per-agent catch writes a file that says
        // what happened instead of one that is silently empty. An empty source file is the one
        // outcome here that looks like success and is not.
        if (authored.Length == 0)
            throw new InvalidOperationException(
                $"The model returned no source for {className}: the whole response was "
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces: "
                + "the tier runs on its model's own ceiling unless the deployment lowers it, so check what the deployment configures.");

        // The constructor belongs to the generated half, which is where the tool's own dependencies
        // are taken and handed to the base class. One written here wins over it and takes neither, so
        // the class stops compiling twice over: the base constructor goes unsatisfied and the field
        // the generated half would have set is left null. Told once and written anyway, the model is
        // told what it did and asked again, the same backstop as the duplicate attribute below.
        if (DeclaresConstructor(authored, className))
        {
            request.AppendLine();
            request.AppendLine($"Your previous answer declared a constructor for {className}. That class already has "
                               + "one, in the half generated beside yours, and it is the only place its dependencies are "
                               + "taken. Write the same file again with no constructor of any kind.");

            // The whole file again, the correction appended to the original request.
            authored = await StreamedCompletion.RunAsync(
                chatClient, system, request.ToString(),
                length => logger.LogInformation(
                    "The mock for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                    agent.ID, length),
                length => logger.LogWarning(
                    "The mock for {AgentId} went silent after {Length} characters; retrying once",
                    agent.ID, length),
                cancellationToken);
        }

        // Thrown rather than returned, for the same reason as an empty answer: a file that says what
        // went wrong is worth more to whoever opens the archive than one that cannot be built.
        if (DeclaresConstructor(authored, className))
            throw new InvalidOperationException(
                $"The model wrote a constructor for {className} twice over. That class takes its dependencies in the "
                + "generated half of the pair and a second constructor leaves the base class unsatisfied, so the "
                + "archive would not build.");

        // The mock without the duplicate attribute the model may still have written, ready for the archive.
        return StripDuplicateToolAttribute(authored);
    }

    /// <summary>
    /// Whether authored source declares a constructor of the class it is one half of.
    /// A declaration opens a line under an access modifier with the class's own name, where a mock building one of its objects never does.
    /// </summary>
    private static bool DeclaresConstructor(string source, string className) =>
        Regex.IsMatch(source,
            $@"^[ \t]*(?:public|internal|protected|private)[ \t]+(?:sealed[ \t]+)?{Regex.Escape(className)}[ \t]*\(",
            RegexOptions.Multiline);

    /// <summary>
    /// Restates each tool's parameters to the mock author in the words the running model will read.
    /// A mock reading what actually arrives works on the first run, where one assuming its own format for the values would not.
    /// </summary>
    private static void AppendArgumentContract(StringBuilder request, AgentDraft agent)
    {
        // Only named tools: an unnamed one has no method in the generated half to implement.
        foreach (ToolDraft tool in agent.Tools.Where(toolDraft => !string.IsNullOrWhiteSpace(toolDraft.Name)))
        {
            // The parameters whose description tells the model what to pass, which is therefore what arrives.
            List<ToolParameterDraft> described =
            [
                .. tool.Parameters.Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !string.IsNullOrWhiteSpace(parameter.Description))
            ];

            // A tool with no described parameter has no contract to restate.
            if (described.Count == 0)
                continue;

            // One block per tool, a line per parameter.
            request.AppendLine();
            request.AppendLine(CultureInfo.InvariantCulture, $"{tool.Name} — what the model is told to pass and therefore what arrives:");

            foreach (ToolParameterDraft parameter in described)
                request.AppendLine(CultureInfo.InvariantCulture, $"  {parameter.Name}: {parameter.Description}");
        }
    }

    /// <summary>
    /// A line carrying the <c>[ProvidesToolForIntent]</c> attribute, which the <c>.g.cs</c> half already puts on this same partial class.
    /// The prompt tells the model a second one is a compile error (CS0579), yet a run has still written it: this is the deterministic backstop,
    /// as <see cref="StreamedCompletion.Unfenced"/> is for a fence the model was equally told not to add.
    /// </summary>
    private static readonly Regex ProvidesToolForIntentLine =
        new(@"^[ \t]*\[ProvidesToolForIntent\([^\n]*\)\][ \t]*\r?\n", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Removes every <c>[ProvidesToolForIntent]</c> line the model wrote, since the generated half already carries the attribute.</summary>
    private static string StripDuplicateToolAttribute(string source) =>
        ProvidesToolForIntentLine.Replace(source, string.Empty);
}