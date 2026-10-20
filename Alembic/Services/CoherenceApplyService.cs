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
/// Applies one coherence finding by running a tool-calling agent whose tools write into the agents the finding names.
/// </summary>
/// <remarks>The agent is built per call because its tools close over that call's draft.</remarks>
public class CoherenceApplyService : ICoherenceApplyService
{
    /// <summary>The <c>alembic.json</c> prompt that composes this pass.</summary>
    private const string PromptId = "CoherenceApplier";

    /// <summary>The tools of this pass: the ones that read and correct one agent then the one that declares the fix applied.</summary>
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

    /// <summary>Resolves the applier's prompt and its tool declarations from <c>alembic.json</c>.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the Efficiency-tier chat client that runs the applier.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records an apply that failed.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the applier over Alembic's prompts and the Efficiency-tier model.
    /// </summary>
    public CoherenceApplyService(
        IAlembicPromptService alembicPromptService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <param name="draft">The domain to fix, changed in place by the tools the model calls.</param>
    /// <param name="finding">The one finding to act on, handed to the model as written.</param>
    /// <param name="cancellationToken">Stops the run when the client leaves the page.</param>
    public async Task<CoherenceApplyResult> ApplyAsync(DomainDraft draft, CoherenceFinding finding, CancellationToken cancellationToken = default)
    {
        // The applier's own prose is read from alembic.json.
        Records.Prompt prompt = alembicPromptService.Resolve(PromptId);

        // The tools write straight into this call's draft.
        CoherenceApplyTools tools = new CoherenceApplyTools(draft, alembicPromptService);

        // A name that the class does not declare throws here instead of reaching the model as a tool that nothing implements.
        MorganaToolAdapter toolAdapter = alembicPromptService.OfferTools(CoherenceApplierStep, tools);

        // The applier rewrites prose that must not contradict itself, so it runs on the Efficiency tier.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        // The agent is the pass: its instructions are the framework primer and the applier's sections, its tools the ones above.
        AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "alembic-coherence-applier",
            Name = "Alembic",
            ChatOptions = new ChatOptions
            {
                // A repair may rewrite whole sections, so the ceiling is Alembic's own.
                MaxOutputTokens = CodeEmitService.OutputCeiling,

                // The primer comes first: striking a sentence that restates a framework rule cannot be told from mutilation without knowing the rules.
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

        // The session holds the single exchange of this finding.
        AgentSession session = await agent.CreateSessionAsync();

        // The finding is handed over field by field so the model does not re-derive it.
        string message = $"Kind: {finding.Kind}\nWhere: {finding.Where}\nWhat: {finding.What}\nWhy: {finding.Why}\nFix: {finding.Fix}";

        try
        {
            // The model reads the finding and corrects the draft through its tools.
            await agent.RunAsync(new ChatMessage(ChatRole.User, message), session, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Applying a coherence finding failed");

            // The client is told why the fix did not land.
            return new CoherenceApplyResult(false, $"The fix could not be applied: {ex.Message}");
        }

        // Only a pass that declared the fix applied reports success; otherwise tool calls made before it stopped may already be on the draft.
        return tools.Summary is { } summary
            ? new CoherenceApplyResult(true, summary)
            : new CoherenceApplyResult(false, "The pass stopped without declaring the fix applied. Some of its tool calls may already have "
                                               + "landed on the Draft — check the affected agents before trusting this finding gone.");
    }
}
