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
/// Authors the working mock of an agent's tool class in one completion on the Efficiency tier.
/// </summary>
/// <remarks>One completion is enough: the input is the generated toolkit and the output is a single file.</remarks>
public class ToolMockService : IToolMockService
{
    /// <summary>The <c>alembic.json</c> prompt that composes the mock author.</summary>
    private const string MockPromptId = "CodeMocker";

    /// <summary>The <c>alembic.json</c> prompt that holds the sentence resuming a cut-off answer.</summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>Resolves the mock author's prompt and injections from <c>alembic.json</c>.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the generated half whose signatures the mock completes.</summary>
    private readonly ICodeEmitService codeEmitService;

    /// <summary>Supplies the Efficiency-tier chat client that writes the mock.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records a mock that was resumed or went silent.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the mock author over the code emit and the Efficiency-tier model.
    /// </summary>
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

    /// <inheritdoc />
    /// <param name="agent">The agent whose toolkit needs a mock body.</param>
    /// <param name="intentName">The intent the agent answers, used to regenerate the signatures the mock completes.</param>
    /// <param name="cancellationToken">Stops the completion when the client leaves the page.</param>
    /// <returns>The whole <c>Tools/*.cs</c> source file, ready to write to the archive.</returns>
    /// <exception cref="InvalidOperationException">The model returned no source or repeated a defect after its retry.</exception>
    public async Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default)
    {
        // The mock author's own prose is read from alembic.json.
        Records.Prompt mock = alembicPromptService.Resolve(MockPromptId);

        // The prompt carries no Morgana voice: this call speaks to nobody and emits a file.
        string system = string.Join("\n\n",
            new[]
            {
                Records.Prompt.Labeled(Constants.SectionLabels.Target, mock.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, mock.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, mock.Formatting)
            }.Where(section => section.Length > 0));

        // The generated half is the specification: it carries the signatures to implement, the class name, the namespace and the base class.
        EmittedFile signatures = codeEmitService.Emit(agent, intentName)
            .First(f => f.Path.Contains("/Tools/", StringComparison.Ordinal) || f.Path.StartsWith("Tools/", StringComparison.Ordinal));

        // The request is the task, the generated half, the contracts of arguments and results and what the agent is for.
        StringBuilder request = new StringBuilder();
        request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "Request"));
        request.AppendLine();
        request.AppendLine(signatures.Content);
        AppendArgumentContract(request, agent);
        AppendReturnContract(request, agent);
        request.AppendLine();
        request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "AgentPurpose", ("target", agent.Target ?? string.Empty)));

        // An agent with a Formatting tells the author how its tool output is presented.
        if (!string.IsNullOrWhiteSpace(agent.Formatting))
        {
            request.AppendLine();
            request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "AgentPresentation", ("formatting", agent.Formatting)));
        }

        // The class the two halves share is named as the generated half declares it, which is what a second declaration would collide with.
        string className = agent.Code.ToolClassName ?? intentName;

        // Mocks are code the client reads and runs on the first turn, so they are written on the Efficiency tier.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        // First attempt at the whole source file.
        string source = await CompleteAsync(chatClient, system, request.ToString(), agent, cancellationToken);

        // An empty source would look like a finished mock: the throw lets the packager's per-agent catch write a file that says what happened.
        if (source.Length == 0)
            throw new InvalidOperationException(
                $"The model returned no source for {className}: the whole response was "
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces — Alembic's "
                + "own ceiling is generous for exactly this reason, so check what the model can emit.");

        // The constructor and the result records belong to the generated half: redeclaring them breaks the build.
        // A type that a record field names exists only if this half declares it.
        // One retry names every defect found, so a file wrong in several ways costs one more completion.
        MockDefects defects = MockDefects.Of(source, agent, className);

        // A source with any defect is asked for again with the defects named.
        if (defects.Any)
        {
            // The constructor takes the tool's dependencies in the generated half, so one written here leaves the base class unsatisfied.
            if (defects.WroteConstructor)
            {
                request.AppendLine();
                request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "ConstructorWritten", ("class", className)));
            }

            // The result records are already declared by the generated half.
            if (defects.RedeclaredRecords.Count > 0)
            {
                request.AppendLine();
                request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "RecordsRedeclared", ("records", string.Join(", ", defects.RedeclaredRecords))));
            }

            // The types that returned fields name are declared only by the half this author writes.
            if (defects.MissingTypes.Count > 0)
            {
                request.AppendLine();
                request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "TypesUndeclared", ("types", string.Join(", ", defects.MissingTypes))));
            }

            // Second attempt, with the defects of the first named in the request.
            source = await CompleteAsync(chatClient, system, request.ToString(), agent, cancellationToken);

            // The second answer is judged on its own: what the first got wrong is no evidence against it.
            defects = MockDefects.Of(source, agent, className);
        }

        // A defect that survives the retry is thrown, so whoever opens the archive finds a file that says what went wrong instead of one that cannot be built.
        if (defects.WroteConstructor)
            throw new InvalidOperationException(
                $"The model wrote a constructor for {className} twice over. That class takes its dependencies in the "
                + "generated half of the pair and a second constructor leaves the base class unsatisfied, so the "
                + "archive would not build.");

        // The second answer still redeclares the generated records.
        if (defects.RedeclaredRecords.Count > 0)
            throw new InvalidOperationException(
                $"The model declared {string.Join(", ", defects.RedeclaredRecords)} twice over. The generated half already declares "
                + "those records, so a second declaration would not build.");

        // The second answer still leaves a returned field's type undeclared.
        if (defects.MissingTypes.Count > 0)
            throw new InvalidOperationException(
                $"The model left {string.Join(", ", defects.MissingTypes)} undeclared twice over. The generated half names those "
                + "types as the type of a returned field and only the half written here can declare them, so the archive would not build.");

        // The mock is handed over without any tool attribute the generated half already carries.
        return StripDuplicateToolAttribute(source);
    }

    /// <summary>
    /// Obtains the source of this agent's mock, logging a resume or a stall against the agent.
    /// </summary>
    private Task<string> CompleteAsync(
        IChatClient chatClient, string system, string request, AgentDraft agent, CancellationToken cancellationToken) =>
        StreamedCompletion.RunAsync(
            chatClient, system, request,
            alembicPromptService.ResolvePromptInjection(AlembicPromptId, "ContinuationRequest"),
            length => logger.LogInformation(
                "The mock for {AgentId} was cut at the provider's limit after {Length} characters; resuming",
                agent.ID, length),
            length => logger.LogWarning(
                "The mock for {AgentId} went silent after {Length} characters; retrying once",
                agent.ID, length),
            cancellationToken);

    /// <summary>
    /// What an authored mock got wrong against the generated half it completes.
    /// </summary>
    /// <param name="WroteConstructor">Whether the source declares the class's constructor, which the generated half owns.</param>
    /// <param name="RedeclaredRecords">The result records the generated half already declares.</param>
    /// <param name="MissingTypes">The types a returned field names that nobody declares.</param>
    private sealed record MockDefects(bool WroteConstructor, List<string> RedeclaredRecords, List<string> MissingTypes)
    {
        /// <summary>Whether any of the three defects is present, which is what makes the source worth another completion.</summary>
        public bool Any => WroteConstructor || RedeclaredRecords.Count > 0 || MissingTypes.Count > 0;

        /// <summary>Reads the three defects off an authored source.</summary>
        public static MockDefects Of(string source, AgentDraft agent, string className) =>
            new(DeclaresConstructor(source, className), RedeclaredResultRecords(source, agent), UndeclaredClientTypes(source, agent));
    }

    /// <summary>Whether authored source declares a constructor of the class it is one half of.</summary>
    /// <remarks>Only a member named after its class under an access modifier counts: a mock builds the class all day.</remarks>
    private static bool DeclaresConstructor(string source, string className) =>
        Regex.IsMatch(source,
            $@"^[ \t]*(?:public|internal|protected|private)[ \t]+(?:sealed[ \t]+)?{Regex.Escape(className)}[ \t]*\(",
            RegexOptions.Multiline);

    /// <summary>
    /// Restates each tool's parameters to the mock author in the words the running model will read.
    /// </summary>
    private void AppendArgumentContract(StringBuilder request, AgentDraft agent)
    {
        // A tool still unnamed in the draft has no contract to restate.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            // Only parameters the client has named and described are restated.
            List<ToolParameterDraft> parameters =
            [
                .. tool.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Description))
            ];

            // A tool without parameters has no argument contract.
            if (parameters.Count == 0)
                continue;

            // The tool is introduced, then each parameter with the description the running model reads.
            request.AppendLine();
            request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "ArgumentContract", ("tool", tool.Name!)));

            foreach (ToolParameterDraft parameter in parameters)
                request.AppendLine(CultureInfo.InvariantCulture, $"  {parameter.Name}: {parameter.Description}");
        }
    }

    /// <summary>
    /// States to the mock author the fields each result record carries, in the words the model reads.
    /// </summary>
    private void AppendReturnContract(StringBuilder request, AgentDraft agent)
    {
        // The fields that a workflow edge carries are flagged, since the mock must fill them for the workflow to move.
        HashSet<(string Tool, string Field)> readByWorkflows = FieldsReadByWorkflows(agent);

        // Only a tool that returns fields has a record to describe.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.Returns.Count > 0))
        {
            // The tool is introduced with the name of the record the generated half declares for it.
            request.AppendLine();
            request.AppendLine(alembicPromptService.ResolvePromptInjection(MockPromptId, "ReturnContract", ("tool", tool.Name!), ("type", CodeEmitService.ResultTypeName(tool.Name!))));

            // Each field is shown with its type and description plus the notes for the failure field and the workflow-carried ones.
            foreach (ToolReturnDraft field in tool.Returns.Where(r => !string.IsNullOrWhiteSpace(r.Name)))
                request.AppendLine(CultureInfo.InvariantCulture,
                    $"  {field.Name} ({field.Type}): {field.Description}{(field.Name == Constants.Workflows.FailureField ? alembicPromptService.ResolvePromptInjection(MockPromptId, "FailureFieldNote") : string.Empty)}{(readByWorkflows.Contains((tool.Name!, field.Name!)) ? alembicPromptService.ResolvePromptInjection(MockPromptId, "WorkflowFieldNote") : string.Empty)}");
        }
    }

    /// <summary>
    /// The result fields that an edge of some workflow carries to the next step, as tool and field.
    /// </summary>
    private static HashSet<(string Tool, string Field)> FieldsReadByWorkflows(AgentDraft agent)
    {
        // The pairs found across every workflow of the agent.
        HashSet<(string Tool, string Field)> fields = [];

        // Every value an edge carries is a field that the next step reads out of the tool's result.
        foreach (Records.WorkflowEdge edge in agent.Workflows.SelectMany(workflow => workflow.Edges))
            foreach (string name in edge.Carrying ?? [])
                // A carried name matches the result field ignoring case, since the workflow names a property and the record a field.
                if (agent.Tools.FirstOrDefault(t => string.Equals(t.Name, edge.Tool, StringComparison.Ordinal)) is { } tool
                    && tool.Returns.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } field)
                    fields.Add((edge.Tool, field.Name!));

        // The tool and field pairs that the mock must fill.
        return fields;
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
    /// Matches the <c>[ProvidesToolForIntent]</c> line that the <c>.g.cs</c> half already carries on the same partial class.
    /// </summary>
    /// <remarks>A second copy of the attribute across the two halves fails the build with CS0579.</remarks>
    private static readonly Regex ProvidesToolForIntentLine =
        new(@"^[ \t]*\[ProvidesToolForIntent\([^\n]*\)\][ \t]*\r?\n", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <inheritdoc cref="ProvidesToolForIntentLine" />
    private static string StripDuplicateToolAttribute(string source) =>
        ProvidesToolForIntentLine.Replace(source, string.Empty);
}
