using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;

namespace Morgana.AI.Commands;

/// <summary>
/// <c>/compact</c>: folds the history of the desk the conversation is with into a summary, so the next
/// turn that desk serves starts from the summary instead of the whole transcript. Nothing leaves the
/// record — a fold moves where a desk's window opens, it does not delete messages — which is why it asks
/// for no confirmation.
/// </summary>
public sealed class CompactHistoryCommand : ICommand
{
    /// <summary>Names the desk the conversation is with, then holds the history this command rewrites.</summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>Turns that desk's name into the type declaring which tier its work is paid on.</summary>
    private readonly IAgentRegistryService agentRegistryService;

    /// <summary>Supplies the client the summarization call runs on.</summary>
    private readonly ILLMService llmService;

    /// <summary>Builds the reducer from the deployment's own history settings, the same one a turn would use.</summary>
    private readonly HistoryReducerService historyReducerService;

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
        IChannelService channelService,
        ILogger logger)
    {
        this.persistenceService = persistenceService;
        this.agentRegistryService = agentRegistryService;
        this.llmService = llmService;
        this.historyReducerService = historyReducerService;
        this.channelService = channelService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public CommandDescriptor Descriptor { get; } = new(
        "compact",
        "Summarize what this agent has been told, so it carries less of it",
        Aliases: ["summarize"],

        // What it folds is a desk's own history: offered while a desk is carrying the conversation, refused
        // otherwise, so it can never reach the welcome and the refusals Morgana writes in her own voice
        RequiresActiveAgent: true);

    /// <inheritdoc />
    public async Task ExecuteAsync(string conversationId, IReadOnlyDictionary<string, string> options)
    {
        // The first frame goes up before anything is looked up: from the user's side the prompt is already
        // held, so a silent wait here would be indistinguishable from a command that did not start
        await SendProgressAsync(conversationId, "reading the conversation");

        // The desk carrying the conversation is the one whose window a fold moves. It is there to be found:
        // the command declares it needs one, so a conversation Morgana is holding herself never gets here
        if (await persistenceService.GetMostRecentActiveAgentAsync(conversationId) is not { Length: > 0 } desk)
        {
            await ClearProgressAsync(conversationId);
            await SendLineAsync(conversationId, "There is nothing to compact: no desk is carrying this conversation right now.");
            return;
        }

        // The second frame names the desk before the summarization call, which is the whole of the wait:
        // what the user watches advance is therefore attributed to whoever is being summarized
        await SendProgressAsync(conversationId, $"summarizing what {desk} carries");

        try
        {
            int foldedMessages = await FoldDeskHistoryAsync(conversationId, desk);

            // The widget comes off the screen before the outcome is written, so the conversation closes on
            // a line rather than on a bar left at its last step
            await ClearProgressAsync(conversationId);

            // A desk still holding a history short enough to read whole folds nothing, which is an answer in
            // itself: the user asked for a saving that turned out not to be needed, not for a failure
            await SendLineAsync(conversationId, foldedMessages == 0
                ? $"Nothing needed compacting: {desk} still carries a history short enough to read whole."
                : $"Compacted {foldedMessages} message{(foldedMessages == 1 ? string.Empty : "s")} of {desk}. Nothing was lost from the transcript.");
        }
        catch (Exception ex)
        {
            // A summarization call that fails, or a row that cannot be rewritten, leaves the desk's history
            // exactly as it was: the fold reaches the record in one write or not at all
            logger.LogError(ex, "Failed to compact the history of '{Desk}' in conversation {ConversationId}", desk, conversationId);

            // The widget belongs to a command that is no longer running, whatever happened to the fold: a bar
            // left standing would hold the prompt on work nobody is doing
            await ClearProgressAsync(conversationId);
            await SendLineAsync(conversationId, $"Compacting {desk} did not go through: its history is untouched.");
        }
    }

    /// <summary>
    /// Folds everything but the recent window of <paramref name="desk"/>'s history and writes it back,
    /// answering how many messages the summary now stands for; zero when there was nothing behind that window.
    /// </summary>
    private async Task<int> FoldDeskHistoryAsync(string conversationId, string desk)
    {
        // The history is taken from the record, which is what a desk reads itself back from: the fold is
        // therefore performed on the same transcript the desk will carry into its next turn
        IReadOnlyList<ChatMessage> history = await persistenceService.LoadParticipantMessagesAsync(conversationId, desk);

        // A desk named by the conversation but holding no row has never been told anything to summarize
        if (history.Count == 0)
            return 0;

        // The reducer is built from the deployment's own history settings, so an on-demand fold keeps
        // exactly the window a turn would have kept. A deployment running its agents on their full history
        // configures none, and there is nothing here to force
        if (historyReducerService.CreateReducer(ChatClientOf(desk)) is not MorganaChatReducer reducer)
        {
            logger.LogInformation("History reduction is disabled, so '{Desk}' has nothing to fold", desk);
            return 0;
        }

        // The summary is stamped onto the message it stands behind, inside the history just read: that mark
        // is what a later reduction reads to know where the desk's window opens. Every message stays
        int foldedMessages = await reducer.CompactAsync(history, CancellationToken.None);

        // A fold that came to nothing leaves the row alone: rewriting it would date a record that did not change
        if (foldedMessages > 0)
            await persistenceService.SaveParticipantMessagesAsync(conversationId, desk, history);

        // What the summary answers for, which the closing line reports as compacted
        return foldedMessages;
    }

    /// <summary>The client the fold is paid on: the tier the desk itself declares, so summarizing costs what that desk costs.</summary>
    private IChatClient ChatClientOf(string desk)
    {
        Records.LLMTier tier = agentRegistryService.ResolveAgentFromIntent(desk)
            ?.GetCustomAttributes(typeof(RequiresLLMTierAttribute), inherit: false)
            .OfType<RequiresLLMTierAttribute>()
            .FirstOrDefault()?.Tier
            // A row left by a desk this installation no longer serves is still summarizable, on the tier
            // every framework call falls back to
            ?? Records.LLMTier.Efficiency;

        return llmService.GetChatClient(tier);
    }

    /// <summary>Pushes one progress frame, with the line that says the same thing where no widget is drawn.</summary>
    private Task SendProgressAsync(string conversationId, string label) =>
        channelService.SendMessageAsync(new ChannelMessage
        {
            ConversationId = conversationId,
            Text = $"Compacting: {label}",
            MessageType = Constants.MessageTypes.System,
            AgentName = Constants.Morgana,
            FadingMessageDurationSeconds = 3,
            // Counted in no steps: the whole wait is one summarization call, which reports nothing of its own
            // progress. A bar filling to a half it would then sit on promises a measure this work has not got
            Progress = new CommandProgress(Descriptor.Name, label, Completed: 0, Total: 0)
        });

    /// <summary>Takes the widget off the screen, whatever the fold behind it came to.</summary>
    private Task ClearProgressAsync(string conversationId) =>
        channelService.SendMessageAsync(new ChannelMessage
        {
            ConversationId = conversationId,
            Text = "Compacting: done",
            MessageType = Constants.MessageTypes.System,
            AgentName = Constants.Morgana,
            FadingMessageDurationSeconds = 3,
            Progress = new CommandProgress(Descriptor.Name, "done", Completed: 0, Total: 0, Finished: true)
        });

    /// <summary>Writes the command's own outcome into the conversation, which is where the user reads it.</summary>
    private Task SendLineAsync(string conversationId, string text) =>
        channelService.SendMessageAsync(new ChannelMessage
        {
            ConversationId = conversationId,
            Text = text,
            MessageType = Constants.MessageTypes.System,
            AgentName = Constants.Morgana
        });
}
