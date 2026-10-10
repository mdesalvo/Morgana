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

    /// <summary>
    /// The tools of this pass: the ones that read and correct one agent, then the one that declares the fix applied.
    /// </summary>
    private static readonly Records.WorkflowStep CoherenceApplierStep = new(
        PromptId,
        [
            nameof(CoherenceApplyTools.GetAgent),
            nameof(CoherenceApplyTools.SetAgentTarget),
            nameof(CoherenceApplyTools.SetAgentInstructions),
            nameof(CoherenceApplyTools.SetAgentFormatting),
            nameof(CoherenceApplyTools.SetIntentDescription),
            nameof(CoherenceApplyTools.DeclareTool),
            nameof(CoherenceApplyTools.SetToolParameter),
            nameof(CoherenceApplyTools.DropToolParameter),
            nameof(CoherenceApplyTools.DropTool),
            nameof(CoherenceApplyTools.ApplyCompleted)
        ]);

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the apply service.
    /// </summary>
    /// <param name="alembicPromptService">Resolves the <c>CoherenceApplier</c> prompt and its tool declarations from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client, always on the Efficiency tier.</param>
    /// <param name="logger">Records a failed apply — the caller also sees it, via the returned result.</param>
    public CoherenceApplyService(
        IAlembicPromptService alembicPromptService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
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
        CoherenceApplyTools tools = new CoherenceApplyTools(draft);

        // A name that the class does not declare throws here instead of reaching the model as a tool that nothing implements.
        MorganaToolAdapter toolAdapter = alembicPromptService.OfferTools(CoherenceApplierStep, tools);

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "alembic-coherence-applier",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                MaxOutputTokens = CodeEmitService.OutputCeiling,

                // The framework first, this pass's own prose under it: what it rewrites is an
                // agent's own prose and the commonest repair — striking a sentence that restates a
                // rule binding above the agent — cannot be told from mutilation without knowing
                // which rules those are.
                Instructions = string.Join("\n\n",
                    new[]
                    {
                        await alembicPromptService.ComposeFrameworkPrimerAsync(),
                        Records.Prompt.Labeled(Constants.SectionLabels.Target, prompt.Target),
                        Records.Prompt.Labeled(Constants.SectionLabels.Instructions, prompt.Instructions),
                        Records.Prompt.Labeled(Constants.SectionLabels.Formatting, prompt.Formatting)
                    }.Where(section => !string.IsNullOrWhiteSpace(section))),
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
