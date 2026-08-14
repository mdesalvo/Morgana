using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="ICoherenceApplyService"/>: a one-shot tool-calling agent, built the same way
/// an interview pass is, whose tools write directly into the agents the finding named.
/// </summary>
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
    public async Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default)
    {
        Records.Prompt prompt = alembicPromptService.Resolve(PromptId);
        List<Records.ToolDefinition> definitions =
            prompt.GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>("Tools", []);

        CoherenceApplyTools tools = new CoherenceApplyTools(draft);
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

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
