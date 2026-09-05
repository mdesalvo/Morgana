using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="ICoherenceApplyService"/>: a one-shot tool-calling agent, built the same way
/// an interview pass is, whose tools write directly into the agents the finding named.
/// </summary>
/// <remarks>
/// One <see cref="AIAgent"/> per call rather than a reused one: the tool set is rebuilt against
/// this call's <see cref="DomainDraft"/> instance every time (<see cref="CoherenceApplyTools"/>
/// closes over it) and a finding is a single, self-contained instruction with no turn after it to
/// keep an agent alive for.
/// </remarks>
public class CoherenceApplyService : ICoherenceApplyService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> governing this pass.
    /// </summary>
    private const string PromptId = "CoherenceApplier";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly IPromptComposerService promptComposerService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the apply service.
    /// </summary>
    /// <param name="alembicPromptService">Resolves the <c>CoherenceApplier</c> prompt and its tool declarations from <c>alembic.json</c>.</param>
    /// <param name="promptComposerService">Passed through to <see cref="MorganaToolAdapter"/> — the framework's own tool-schema machinery.</param>
    /// <param name="llmService">Supplies the chat client, always on the Performance tier.</param>
    /// <param name="logger">Records a failed apply — the caller also sees it, via the returned result.</param>
    public CoherenceApplyService(
        IAlembicPromptService alembicPromptService,
        IPromptComposerService promptComposerService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.promptComposerService = promptComposerService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <param name="draft">The domain to fix, mutated in place by whichever tools the model calls —
    /// there is no copy: a successful apply is the caller's own Draft with the fix already on it.</param>
    /// <param name="finding">The one coherence finding to act on, rendered into the opening user
    /// message below (Kind/Where/What/Why/Fix) rather than left for the model to re-derive.</param>
    /// <param name="cancellationToken">Cancels the underlying run.</param>
    /// <returns>Whether the pass declared the fix applied (via <c>ApplyCompleted</c>) and a one-line
    /// summary of what changed — or, on failure, an explanation of what could not be done. A
    /// declared-false-but-tool-calls-may-have-landed result is possible: see the final return below.</returns>
    public async Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default)
    {
        Records.Prompt prompt = alembicPromptService.Resolve(PromptId);
        List<Records.ToolDefinition> definitions =
            prompt.GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>("Tools", []);

        CoherenceApplyTools tools = new CoherenceApplyTools(draft);
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

        // Maps each tool's name in alembic.json to the CoherenceApplyTools method that implements
        // it. The lookup below turns a declaration with no matching method into a startup-time
        // failure instead of a schema the model is offered and can never actually call.
        Dictionary<string, Delegate> implementations = new(StringComparer.Ordinal)
        {
            [nameof(CoherenceApplyTools.GetAgent)] = tools.GetAgent,
            [nameof(CoherenceApplyTools.SetAgentTarget)] = tools.SetAgentTarget,
            [nameof(CoherenceApplyTools.SetAgentInstructions)] = tools.SetAgentInstructions,
            [nameof(CoherenceApplyTools.SetAgentFormatting)] = tools.SetAgentFormatting,
            [nameof(CoherenceApplyTools.SetIntentDescription)] = tools.SetIntentDescription,
            [nameof(CoherenceApplyTools.DeclareTool)] = tools.DeclareTool,
            [nameof(CoherenceApplyTools.SetToolParameter)] = tools.SetToolParameter,
            [nameof(CoherenceApplyTools.DropToolParameter)] = tools.DropToolParameter,
            [nameof(CoherenceApplyTools.DropTool)] = tools.DropTool,
            [nameof(CoherenceApplyTools.ApplyCompleted)] = tools.ApplyCompleted
        };

        foreach (Records.ToolDefinition definition in definitions)
        {
            if (!implementations.TryGetValue(definition.Name, out Delegate? implementation))
                throw new InvalidOperationException(
                    $"alembic.json declares tool '{definition.Name}' for '{PromptId}', but CoherenceApplyTools has no method by that name.");

            toolAdapter.AddTool(definition.Name, implementation, definition);
        }

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "alembic-coherence-applier",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                Instructions = string.Join("\n\n", new[] { prompt.Target, prompt.Instructions, prompt.Formatting }
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                Tools = [.. await toolAdapter.CreateAllFunctionsAsync()]
            }
        });

        AgentSession session = await agent.CreateSessionAsync();

        string message = $"Kind: {finding.Kind}\nWhere: {finding.Where}\nWhat: {finding.What}\nWhy: {finding.Why}\nFix: {finding.Fix}";

        try
        {
            await agent.RunAsync(new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Applying a coherence finding failed");
            return new CoherenceApplyResult(false, $"The fix could not be applied: {ex.Message}");
        }

        return tools.Summary is { } summary
            ? new CoherenceApplyResult(true, summary)
            : new CoherenceApplyResult(false, "The pass stopped without declaring the fix applied. Some of its tool calls may already have "
                                               + "landed on the Draft — check the affected agents before trusting this finding gone.");
    }
}
