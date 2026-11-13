using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="ICoherenceApplyService"/>: a one-shot tool-calling agent, built as an interview pass is, whose tools write into the agents the finding names.
/// A new agent is built per call, since its tools close over this call's Draft and a finding is one instruction with no turn after it.
/// </summary>
public class CoherenceApplyService : ICoherenceApplyService
{
    /// <summary>The prompt in <c>alembic.json</c> governing this pass.</summary>
    private const string PromptId = "CoherenceApplier";

    /// <summary>Resolves the pass's prompt and the framework primer it reads first.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>The framework's composer, which <see cref="MorganaToolAdapter"/> describes the tools through.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records an apply that failed, which the caller also sees in the result.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the pass to its prompts, the tool machinery and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>CoherenceApplier</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="promptComposerService">Passed to <see cref="MorganaToolAdapter"/>, the framework's own tool-schema machinery.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records a failed apply.</param>
    public CoherenceApplyService(
        IAlembicPromptService alembicPromptService,
        IPromptComposerService promptComposerService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.promptComposerService = promptComposerService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>
    /// Applies one finding's fix through the pass's tools, which change the caller's own Draft in place.
    /// A result declared not applied may still follow tool calls that landed, which its summary tells the client to check.
    /// </summary>
    /// <param name="draft">The domain to fix, changed in place by whichever tools the model calls.</param>
    /// <param name="finding">The finding to act on, handed to the model field by field rather than left for it to re-derive.</param>
    /// <param name="cancellationToken">Cancels the underlying run.</param>
    /// <returns>Whether the pass declared the fix applied with what changed; otherwise why nothing could be done.</returns>
    public async Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default)
    {
        // The pass's own prose: what it is for, how it repairs and how it reports.
        Records.Prompt prompt = alembicPromptService.Resolve(PromptId);

        // The tools write into this very Draft, so the fix lands on what the client is looking at.
        CoherenceApplyTool tools = new CoherenceApplyTool(draft);
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter(promptComposerService);

        // The ten tools of the apply pass, each described by the attributes of the method that implements it.
        toolAdapter.AddTools(tools, MorganaToolAdapter.ProjectToolDefinitions(typeof(CoherenceApplyTool)));

        // Rewriting an agent's prose is the same job as writing it, so it runs on the tier the interview does.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "alembic-coherence-applier",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
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

                // The pass may change exactly what its tools reach and nothing else.
                Tools = [.. await toolAdapter.CreateAllFunctionsAsync()]
            }
        });

        // A session of its own: the apply remembers nothing of the coherence pass that produced the finding.
        AgentSession session = await agent.CreateSessionAsync();

        // The finding exactly as the client read it, so the pass acts on what they decided to apply.
        string message = $"Kind: {finding.Kind}\nWhere: {finding.Where}\nWhat: {finding.What}\nWhy: {finding.Why}\nFix: {finding.Fix}";

        try
        {
            // One run: the model reads the agents involved, writes the fix and declares it done.
            await agent.RunAsync(new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed run is reported on the finding's own row, so the client knows it is still open.
            logger.LogError(ex, "Applying a coherence finding failed");
            return new CoherenceApplyResult(false, $"The fix could not be applied: {ex.Message}");
        }

        // Only ApplyCompleted sets a summary, so its absence means the pass stopped without declaring the fix done.
        return tools.Summary is { } summary
            ? new CoherenceApplyResult(true, summary)
            : new CoherenceApplyResult(false, "The pass stopped without declaring the fix applied. Some of its tool calls may already have "
                                               + "landed on the Draft — check the affected agents before trusting this finding gone.");
    }
}