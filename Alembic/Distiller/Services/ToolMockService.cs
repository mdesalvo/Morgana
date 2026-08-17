using System.Text;
using System.Text.RegularExpressions;
using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="IToolMockService"/>: one completion per agent, on the Performance tier.
/// </summary>
/// <remarks>
/// A single completion rather than an agent with tools, because nothing here is a conversation:
/// there is one input, the toolkit, and one output, a file. The interview needed tools to hold a
/// state machine steady across many turns; this needs none of that, and giving it an agent would be
/// machinery in place of a call.
/// </remarks>
public class ToolMockService : IToolMockService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs mock authoring.
    /// </summary>
    private const string MockPromptId = "CodeMocker";

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
            new[] { mock.Target, mock.Instructions, mock.Formatting }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // The generated half IS the specification: it carries the exact signatures the answer must
        // implement, the class name, the namespace and the base class. Describing them in prose as
        // well would be a second, drifting statement of something already exact.
        EmittedFile signatures = codeEmitService.Emit(agent, intentName)
            .First(f => f.Path.Contains("/Tools/", StringComparison.Ordinal) || f.Path.StartsWith("Tools/", StringComparison.Ordinal));

        StringBuilder request = new StringBuilder();
        request.AppendLine("This is the generated half of the tool class. Write the other half.");
        request.AppendLine();
        request.AppendLine(signatures.Content);
        AppendArgumentContract(request, agent);
        request.AppendLine();
        request.AppendLine($"The agent this toolkit belongs to exists for: {agent.Target}");

        if (!string.IsNullOrWhiteSpace(agent.Formatting))
        {
            request.AppendLine();
            request.AppendLine($"It presents what these tools return like this, so return data that makes it possible: {agent.Formatting}");
        }

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
                $"The model returned no source for {agent.Code.ToolClassName ?? intentName}: the whole response was "
                + "reasoning and no text. That is what a MaxOutputTokens too small for a source file produces — Alembic's "
                + "own tier declares a generous one for exactly this reason, so check what the deployment configures.");

        return StripDuplicateToolAttribute(authored);
    }

    /// <summary>
    /// Restates each tool's parameters to the mock author in the words the running model will read.
    /// </summary>
    private static void AppendArgumentContract(StringBuilder request, AgentDraft agent)
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
            request.AppendLine($"{tool.Name} — what the model is told to pass, and therefore what arrives:");

            foreach (ToolParameterDraft parameter in described)
                request.AppendLine($"  {parameter.Name}: {parameter.Description}");
        }
    }

    /// <summary>
    /// The <c>[ProvidesToolForIntent]</c> attribute the <c>.g.cs</c> half already carries on this
    /// same partial class.
    /// </summary>
    /// <remarks>
    /// <c>CodeMocker</c>'s own prompt already tells the model this is a compile error and to write
    /// no attribute at all — and an observed run still wrote one anyway (<c>CS0579</c>, a duplicate
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
