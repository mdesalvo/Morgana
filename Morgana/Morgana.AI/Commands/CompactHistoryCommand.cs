using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;

namespace Morgana.AI.Commands;

/// <summary>
/// <c>/compact</c>: folds the history of the agent the conversation is with into a summary, so the next
/// turn that agent serves starts from the summary instead of the whole transcript. Nothing leaves the
/// record — a fold moves where an agent's window opens, it does not delete messages — which is why it asks
/// for no confirmation.
/// </summary>
public sealed class CompactHistoryCommand : ICommand
{
    /// <summary>The steps the command reports: reading the conversation on record, then the fold itself.</summary>
    private const int ProgressSteps = 2;

    /// <summary>Names the agent the conversation is with, then holds the history this command rewrites.</summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>Turns that agent's name into the type declaring which tier its work is paid on.</summary>
    private readonly IAgentRegistryService agentRegistryService;

    /// <summary>Supplies the client the summarization call runs on.</summary>
    private readonly ILLMService llmService;

    /// <summary>Builds the reducer from the deployment's own history settings, the same one a turn would use.</summary>
    private readonly HistoryReducerService historyReducerService;

    /// <summary>Charges the fold to the conversation, as every other call to a model is charged.</summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>Carries the progress frames and the closing line to whoever asked.</summary>
    private readonly IChannelService channelService;

    /// <summary>Reports a fold that could not be completed, which the user is told about in one line only.</summary>
    private readonly ILogger logger;

    /// <summary>Captures the record, the agent registry, the LLM tiers, the reducer factory and the channel.</summary>
    public CompactHistoryCommand(
        IConversationPersistenceService persistenceService,
        IAgentRegistryService agentRegistryService,
        ILLMService llmService,
        HistoryReducerService historyReducerService,
        IDustLimitService dustLimitService,
        IChannelService channelService,
        ILogger logger)
    {
        this.persistenceService = persistenceService;
        this.agentRegistryService = agentRegistryService;
        this.llmService = llmService;
        this.historyReducerService = historyReducerService;
        this.dustLimitService = dustLimitService;
        this.channelService = channelService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public CommandDescriptor Descriptor { get; } = new(
        "compact",
        "Summarize what this agent has been told, so it carries less of it",

        // What it folds is an agent's own history: offered while an agent is carrying the conversation, refused
        // otherwise, so it can never reach the welcome and the refusals Morgana writes in her own voice
        RequiresActiveAgent: true);

    /// <inheritdoc />
    public async Task ExecuteAsync(string conversationId, string? invocationId, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken)
    {
        // The first frame goes up before anything is looked up: from the user's side the prompt is already
        // held, so a silent wait here would be indistinguishable from a command that did not start
        await SendProgressAsync(conversationId, invocationId, "reading the conversation", completed: 0);

        // The agent carrying the conversation is the one whose window a fold moves. It is there to be found:
        // the command declares it needs one, so a conversation Morgana is holding herself never gets here
        if (await persistenceService.GetMostRecentActiveAgentAsync(conversationId) is not { Length: > 0 } agent)
        {
            await SendOutcomeAsync(conversationId, invocationId, "There is nothing to compact: no agent is carrying this conversation right now.");
            return;
        }

        try
        {
            // A channel that gave up during the lookup must not see a widget come back for a command it
            // already reported as timed out
            cancellationToken.ThrowIfCancellationRequested();

            // The second frame names the agent before the summarization call, which is the whole of the wait:
            // what the user watches advance is therefore attributed to whoever is being summarized
            await SendProgressAsync(conversationId, invocationId, $"summarizing what {agent} carries", completed: 1);

            int foldedMessages = await FoldAgentHistoryAsync(conversationId, agent, cancellationToken);

            // An agent still holding a history short enough to read whole folds nothing, which is an answer in
            // itself: the user asked for a saving that turned out not to be needed, not for a failure
            await SendOutcomeAsync(conversationId, invocationId, foldedMessages == 0
                ? $"Nothing needed compacting: {agent} still carries a history short enough to read whole."
                : $"Compacted {foldedMessages} message{(foldedMessages == 1 ? string.Empty : "s")} of {agent}. Nothing was lost from the transcript.");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // The channel has already told the user the command was called off and freed the prompt: any frame
            // or line sent now would land as a late contradiction. Whatever a provider throws once the call is
            // dropped is the same abandonment, so it is not reported as a failure either
            logger.LogInformation("Compacting '{Agent}' in conversation {ConversationId} was abandoned by the channel; its history is untouched", agent, conversationId);
        }
        catch (Exception ex)
        {
            // A summarization call that fails or a row that cannot be rewritten leaves the agent's history
            // exactly as it was: the fold reaches the record in one write or not at all
            logger.LogError(ex, "Failed to compact the history of '{Agent}' in conversation {ConversationId}", agent, conversationId);

            await SendOutcomeAsync(conversationId, invocationId, $"Compacting {agent} did not go through: its history is untouched.");
        }
    }

    /// <summary>
    /// Folds everything but the recent window of <paramref name="agent"/>'s history and writes it back,
    /// answering how many messages the summary now stands for; zero when there was nothing behind that window.
    /// </summary>
    private async Task<int> FoldAgentHistoryAsync(string conversationId, string agent, CancellationToken cancellationToken)
    {
        // The history is taken from the record, which is what an agent reads itself back from: the fold is
        // therefore performed on the same transcript the agent will carry into its next turn
        IReadOnlyList<ChatMessage> history = await persistenceService.LoadParticipantMessagesAsync(conversationId, agent);

        // An agent named by the conversation but holding no row has never been told anything to summarize
        if (history.Count == 0)
            return 0;

        // The reducer is built from the deployment's own history settings, so an on-demand fold keeps
        // exactly the window a turn would have kept. A deployment running its agents on their full history
        // configures none, leaving nothing here to force
        if (historyReducerService.CreateReducer(MeteredChatClientOf(agent, conversationId)) is not MorganaChatReducer reducer)
        {
            logger.LogInformation("History reduction is disabled, so '{Agent}' has nothing to fold", agent);
            return 0;
        }

        // The summary is stamped onto the message it stands behind, inside the history just read: that mark
        // is what a later reduction reads to know where the agent's window opens. Every message stays
        int foldedMessages = await reducer.CompactAsync(history, cancellationToken);

        // A fold that came to nothing leaves the row alone: rewriting it would date a record that did not change
        if (foldedMessages == 0)
            return 0;

        // The last point the fold can be dropped: the summary lives only in the history read above and the
        // call that composed it is already on the ledger, as spent tokens are. Past here the save is one
        // transaction nothing interrupts, so a channel giving up during it is told what was written
        cancellationToken.ThrowIfCancellationRequested();

        // The fold took a call to a model to compose, during which the agent may have served a turn. What it
        // wrote is kept; what this command read is what it speaks for. A row it can no longer speak for is
        // left as the agent made it — a saving is never worth a message
        if (!await persistenceService.SaveParticipantMessagesAsync(conversationId, agent, history, history.Count))
        {
            logger.LogWarning("'{Agent}' wrote to conversation {ConversationId} while it was being folded; the fold was dropped", agent, conversationId);
            return 0;
        }

        // What the summary answers for, which the closing line reports as compacted
        return foldedMessages;
    }

    /// <summary>
    /// The client the fold is paid on: the tier the agent itself declares, so summarizing costs what that
    /// agent costs, metered like every other call to a model — a saving that spent tokens off the books
    /// would leave the conversation's budget claiming more than it has.
    /// </summary>
    private IChatClient MeteredChatClientOf(string agent, string conversationId)
    {
        Records.LLMTier tier = agentRegistryService.ResolveAgentFromIntent(agent)
            ?.GetCustomAttributes(typeof(RequiresLLMTierAttribute), inherit: false)
            .OfType<RequiresLLMTierAttribute>()
            .FirstOrDefault()?.Tier
            // A row left by an agent this installation no longer serves is still summarizable, on the tier
            // every framework call falls back to
            ?? Records.LLMTier.Efficiency;

        // Charged under the agent whose history is being folded, beside that agent's own turns on the ledger
        return new DustAccountingChatClient(
            llmService.GetChatClient(tier),
            dustLimitService,
            llmService.GetPricing(tier),
            $"{Constants.Morgana} ({char.ToUpperInvariant(agent[0])}{agent[1..]}/{tier})",
            conversationId);
    }

    /// <summary>Pushes one progress frame of the run <paramref name="invocationId"/> names, with the line that says the same thing where no widget is drawn.</summary>
    private Task SendProgressAsync(string conversationId, string? invocationId, string label, int completed) =>
        channelService.SendMessageAsync(new ChannelMessage
        {
            ConversationId = conversationId,
            Text = $"Compacting: {label} ({completed + 1}/{ProgressSteps})",
            MessageType = Constants.MessageTypes.System,
            AgentName = Constants.Morgana,
            FadingMessageDurationSeconds = 3,
            // The steps are the command's own: what it has already done, out of what it set out to do
            Progress = new CommandProgress(Descriptor.Name, label, completed, ProgressSteps, InvocationId: invocationId)
        });

    /// <summary>
    /// Sends the finished frame carrying the command's outcome, whatever the fold came to: the widget comes off
    /// the screen and the outcome takes its place in one delivery, so no bar is left standing on work nobody is doing.
    /// </summary>
    private Task SendOutcomeAsync(string conversationId, string? invocationId, string outcome) =>
        channelService.SendMessageAsync(new ChannelMessage
        {
            ConversationId = conversationId,
            Text = outcome,
            MessageType = Constants.MessageTypes.System,
            AgentName = Constants.Morgana,
            Progress = new CommandProgress(Descriptor.Name, "done", ProgressSteps, ProgressSteps, Finished: true, invocationId)
        });
}
