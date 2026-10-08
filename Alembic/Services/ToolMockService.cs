using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IToolMockService"/>: one completion per agent, on the Performance tier.
/// </summary>
/// <remarks>
/// A single completion rather than an agent with tools, because nothing here is a conversation:
/// there is one input, the toolkit and one output, a file. The interview needed tools to hold a
/// state machine steady across many turns; this needs none of that and giving it an agent would be
/// machinery in place of a call.
/// </remarks>
public class ToolMockService : IToolMockService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs mock authoring.
    /// </summary>
    private const string MockPromptId = "CodeMocker";

    /// <summary>The value of the agent's Target, spliced into the <c>AgentPurpose</c> message.</summary>
    private const string TargetPlaceholder = "((target))";

    /// <summary>The value of the agent's Formatting, spliced into the <c>AgentPresentation</c> message.</summary>
    private const string FormattingPlaceholder = "((formatting))";

    /// <summary>The name of the tool that a contract message is about.</summary>
    private const string ToolPlaceholder = "((tool))";

    /// <summary>The name of the result record that the generated half declares for a tool.</summary>
    private const string TypePlaceholder = "((type))";

    /// <summary>The name of the tool class that the two halves share.</summary>
    private const string ClassPlaceholder = "((class))";

    /// <summary>The result records that the authored source declared a second time.</summary>
    private const string RecordsPlaceholder = "((records))";

    /// <summary>The types that a returned field names and that the authored source left undeclared.</summary>
    private const string TypesPlaceholder = "((types))";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ICodeEmitService codeEmitService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the mock service.
    /// </summary>
    /// <param name="alembicPromptService">Resolves the <c>CodeMocker</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="codeEmitService">Supplies the generated tool signatures the mock must implement.</param>
    /// <param name="llmService">Supplies the chat client, always on the Performance tier.</param>
    /// <param name="logger">Records a resumed (cut-off) generation — the caller's own progress signal.</param>
    public ToolMockService(
        IAlembicPromptService alembicPromptService,
        ICodeEmitService codeEmitService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.codeEmitService = codeEmitService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <param name="agent">The agent whose toolkit needs a mock body — read for its Target, Formatting and tools.</param>
    /// <param name="intentName">The intent this agent answers, passed through to <see cref="ICodeEmitService.Emit"/>
    /// to regenerate the same tool signatures the caller already has, rather than threading them through as a parameter.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>The whole <c>Tools/*.cs</c> source file, fence-stripped and ready to write to the archive.</returns>
    /// <exception cref="InvalidOperationException">The model returned no text at all — see the remarks below.</exception>
    public async Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default)
    {
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

        // The generated half IS the specification: it carries the exact signatures the answer must
        // implement, the class name, the namespace and the base class. Describing them in prose as
        // well would be a second, drifting statement of something already exact.
        EmittedFile signatures = codeEmitService.Emit(agent, intentName)
            .First(f => f.Path.Contains("/Tools/", StringComparison.Ordinal) || f.Path.StartsWith("Tools/", StringComparison.Ordinal));

        StringBuilder request = new StringBuilder();
        request.AppendLine(Said(mock, "Request"));
        request.AppendLine();
        request.AppendLine(signatures.Content);
        AppendArgumentContract(request, mock, agent);
        AppendReturnContract(request, mock, agent);
        request.AppendLine();
        request.AppendLine(Said(mock, "AgentPurpose", (TargetPlaceholder, agent.Target ?? string.Empty)));

        if (!string.IsNullOrWhiteSpace(agent.Formatting))
        {
            request.AppendLine();
            request.AppendLine(Said(mock, "AgentPresentation", (FormattingPlaceholder, agent.Formatting)));
        }

        // The class the two halves share, named by the fact the emit already carries rather than
        // guessed: what the generated half declares is what a second declaration would collide with.
        string className = agent.Code.ToolClassName ?? intentName;

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

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
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces — Alembic's "
                + "own tier declares a generous one for exactly this reason, so check what the deployment configures.");

        // One retry covers every problem the first answer has, so a file wrong twice over costs one
        // more completion and not two. The constructor belongs to the generated half, which is where
        // the tool's own dependencies are taken and handed to the base class: one written here wins
        // over it and takes neither, so the base constructor goes unsatisfied. The result records are
        // declared there too, so a second declaration collides with them. A type that a record names
        // and nobody declares is the opposite case: it exists only if this half declares it. Told once
        // and written anyway, the model is told what it did and asked again.
        List<string> redeclaredRecords = RedeclaredResultRecords(authored, agent);
        List<string> missingTypes = UndeclaredClientTypes(authored, agent);
        bool wroteConstructor = DeclaresConstructor(authored, className);

        if (wroteConstructor || redeclaredRecords.Count > 0 || missingTypes.Count > 0)
        {
            if (wroteConstructor)
            {
                request.AppendLine();
                request.AppendLine(Said(mock, "ConstructorWritten", (ClassPlaceholder, className)));
            }

            if (redeclaredRecords.Count > 0)
            {
                request.AppendLine();
                request.AppendLine(Said(mock, "RecordsRedeclared", (RecordsPlaceholder, string.Join(", ", redeclaredRecords))));
            }

            if (missingTypes.Count > 0)
            {
                request.AppendLine();
                request.AppendLine(Said(mock, "TypesUndeclared", (TypesPlaceholder, string.Join(", ", missingTypes))));
            }

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

        redeclaredRecords = RedeclaredResultRecords(authored, agent);
        if (redeclaredRecords.Count > 0)
            throw new InvalidOperationException(
                $"The model declared {string.Join(", ", redeclaredRecords)} twice over. The generated half already declares "
                + "those records, so a second declaration would not build.");

        missingTypes = UndeclaredClientTypes(authored, agent);
        if (missingTypes.Count > 0)
            throw new InvalidOperationException(
                $"The model left {string.Join(", ", missingTypes)} undeclared twice over. The generated half names those "
                + "types as the type of a returned field and only the half written here can declare them, so the archive would not build.");

        return StripDuplicateToolAttribute(authored);
    }

    /// <summary>
    /// Fetches a text that the mock author reads from the prompt's <c>PromptInjections</c>, with its values spliced in.
    /// </summary>
    /// <remarks>
    /// A missing message throws: an author handed a request with a sentence absent would write the wrong file without any sign of it.
    /// </remarks>
    /// <param name="mock">The <c>CodeMocker</c> prompt, which carries the message.</param>
    /// <param name="name">Which message.</param>
    /// <param name="values">Placeholder to the value that it stands for.</param>
    private static string Said(Records.Prompt mock, string name, params (string Placeholder, string Value)[] values)
    {
        string text = Records.Injection.ResolveTemplate(
            mock.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.PromptInjections), name);

        if (text.Length == 0)
            throw new InvalidOperationException($"The {MockPromptId} prompt in alembic.json declares no '{name}' message.");

        foreach ((string placeholder, string value) in values)
            text = text.Replace(placeholder, value, StringComparison.Ordinal);

        return text;
    }

    /// <summary>Whether authored source declares a constructor of the class it is one half of.</summary>
    /// <remarks>
    /// A declaration is recognised where a method never could be: a member named after its own class,
    /// opening a line under an access modifier. Anything else naming the class is somebody building
    /// one, which is what a mock does all day.
    /// </remarks>
    private static bool DeclaresConstructor(string source, string className) =>
        Regex.IsMatch(source,
            $@"^[ \t]*(?:public|internal|protected|private)[ \t]+(?:sealed[ \t]+)?{Regex.Escape(className)}[ \t]*\(",
            RegexOptions.Multiline);

    /// <summary>
    /// Restates each tool's parameters to the mock author in the words the running model will read.
    /// </summary>
    private static void AppendArgumentContract(StringBuilder request, Records.Prompt mock, AgentDraft agent)
    {
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            List<ToolParameterDraft> described =
            [
                .. tool.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Description))
            ];

            if (described.Count == 0)
                continue;

            request.AppendLine();
            request.AppendLine(Said(mock, "ArgumentContract", (ToolPlaceholder, tool.Name!)));

            foreach (ToolParameterDraft parameter in described)
                request.AppendLine(CultureInfo.InvariantCulture, $"  {parameter.Name}: {parameter.Description}");
        }
    }

    /// <summary>
    /// States to the mock author the fields each result record carries, in the words the model reads.
    /// </summary>
    private static void AppendReturnContract(StringBuilder request, Records.Prompt mock, AgentDraft agent)
    {
        HashSet<(string Tool, string Field)> readByWorkflows = FieldsReadByWorkflows(agent);

        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.Returns.Count > 0))
        {
            request.AppendLine();
            request.AppendLine(Said(mock, "ReturnContract", (ToolPlaceholder, tool.Name!), (TypePlaceholder, CodeEmitService.ResultTypeName(tool.Name!))));

            foreach (ToolReturnDraft field in tool.Returns.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
                request.AppendLine(CultureInfo.InvariantCulture,
                    $"  {field.Name} ({field.Type}): {field.Description}{(field.Name == Constants.Workflows.FailureField ? Said(mock, "FailureFieldNote") : string.Empty)}{(readByWorkflows.Contains((tool.Name!, field.Name!)) ? Said(mock, "WorkflowFieldNote") : string.Empty)}");
        }
    }

    /// <summary>
    /// The result fields that an edge of some workflow carries to the next step, as tool and field.
    /// </summary>
    /// <remarks>
    /// A carried name is matched to the tool's field regardless of case, because the workflow class
    /// names it as a property and the result record names it as a field.
    /// </remarks>
    private static HashSet<(string Tool, string Field)> FieldsReadByWorkflows(AgentDraft agent)
    {
        HashSet<(string Tool, string Field)> read = [];

        foreach (Records.WorkflowEdge edge in agent.Workflows.SelectMany(workflow => workflow.Edges))
            foreach (string carried in edge.Carrying ?? [])
                if (agent.Tools.FirstOrDefault(t => string.Equals(t.Name, edge.Tool, StringComparison.Ordinal)) is { } tool
                    && tool.Returns.FirstOrDefault(r => string.Equals(r.Name, carried, StringComparison.OrdinalIgnoreCase)) is { } field)
                    read.Add((edge.Tool, field.Name!));

        return read;
    }

    /// <summary>
    /// The result records that the generated half declares and the authored source declares a second time.
    /// </summary>
    private static List<string> RedeclaredResultRecords(string source, AgentDraft agent) =>
        [.. agent.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => CodeEmitService.ResultTypeName(t.Name!))
            .Where(name => DeclaresType(source, name))];

    /// <summary>
    /// The types that a returned field names and that neither the generated half nor the authored source declares.
    /// </summary>
    /// <remarks>
    /// The generated half declares only the result records, so a record that a field refers to
    /// (a list of catalog entries) exists only if the half the client owns declares it.
    /// </remarks>
    private static List<string> UndeclaredClientTypes(string source, AgentDraft agent) =>
        [.. agent.Tools
            .SelectMany(t => t.Returns)
            .SelectMany(field => CodeEmitService.ClientTypeNames(field.Type))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !agent.Tools.Any(t => !string.IsNullOrWhiteSpace(t.Name) && CodeEmitService.ResultTypeName(t.Name!) == name))
            .Where(name => !DeclaresType(source, name))];

    /// <summary>
    /// Whether the source declares a type of that name.
    /// </summary>
    private static bool DeclaresType(string source, string name) =>
        Regex.IsMatch(source, $@"\b(?:record|class|struct|enum|interface)\s+(?:class\s+|struct\s+)?{Regex.Escape(name)}\b");

    /// <summary>
    /// The <c>[ProvidesToolForIntent]</c> attribute the <c>.g.cs</c> half already carries on this
    /// same partial class.
    /// </summary>
    /// <remarks>
    /// <c>CodeMocker</c>'s own prompt already tells the model this is a compile error and to write
    /// no attribute at all; an observed run still wrote one anyway (<c>CS0579</c>, a duplicate
    /// attribute across the two partial declarations). Prose that has already failed once empirically
    /// is not made more reliable by restating it more emphatically; this is the deterministic backstop,
    /// the same reasoning as <see cref="StreamedCompletion.Unfenced"/> stripping a markdown fence the
    /// model was equally told not to add.
    /// </remarks>
    private static readonly Regex ProvidesToolForIntentLine =
        new(@"^[ \t]*\[ProvidesToolForIntent\([^\n]*\)\][ \t]*\r?\n", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <inheritdoc cref="ProvidesToolForIntentLine" />
    private static string StripDuplicateToolAttribute(string source) =>
        ProvidesToolForIntentLine.Replace(source, string.Empty);
}
