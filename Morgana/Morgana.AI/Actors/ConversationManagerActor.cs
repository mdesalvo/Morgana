using System.Globalization;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Actors;

/// <summary>
/// The conversation's entry point: it starts and ends the conversation, hands each message to the supervisor
/// and delivers every answer to the client, the dust warnings included.
/// </summary>
public class ConversationManagerActor : MorganaActor
{
    /// <summary>The adapting channel, which fits every answer to the conversation's capabilities before the transport carries it.</summary>
    private readonly IChannelService channelService;

    /// <summary>
    /// The conversation's channel, settled at the handshake before this actor exists.
    /// This actor only drops the in-memory copy when the conversation stops being served here.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>The conversation's dust budget, read around each answer for its gauge, its warnings and its lockout.</summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>The dust-limiting switch and the authored texts of its warnings and of its lockout.</summary>
    private readonly Records.DustLimitingOptions dustLimitingOptions;

    /// <summary>
    /// Keeps Morgana's own side of the conversation: every answer that passes through here without an agent having recorded it is hers.
    /// A transcript that lacked it would show the user a question of theirs with nothing under it.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// The instant the last answer went out under.
    /// Kept so a turn closing with two answers dates them apart: a client takes anything dated no later than what it holds as a repeat.
    /// </summary>
    private DateTime lastAnswerTimestamp = DateTime.MinValue;

    /// <summary>
    /// The conversation's supervisor; null until the conversation starts here or its first message reaches this process.
    /// The supervisor dying clears it again.
    /// </summary>
    private IActorRef? supervisor;

    /// <summary>Binds the manager to its conversation and to the services that deliver and record its answers.</summary>
    public ConversationManagerActor(
        string conversationId,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IDustLimitService dustLimitService,
        IConversationPersistenceService conversationPersistenceService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The services arrive from the actor system's DI scope and live as long as the actor.
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.dustLimitService = dustLimitService;
        this.conversationPersistenceService = conversationPersistenceService;
        this.dustLimitingOptions = dustLimitingOptions.Value;

        // A user message starts a turn on the supervisor, which is brought up first when missing.
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        // The controller starts the conversation, which brings up the supervisor and has Morgana present herself.
        ReceiveAsync<Records.CreateConversation>(HandleCreateConversationAsync);

        // The controller ends the conversation, which stops the supervisor.
        ReceiveAsync<Records.TerminateConversation>(HandleTerminateConversationAsync);

        // A whole answer of the turn reaches the client, recorded and followed by the dust notices.
        ReceiveAsync<Records.ConversationResponse>(HandleConversationResponseAsync);

        // A streamed piece of the answer reaches the client as it arrives.
        ReceiveAsync<Records.AgentStreamChunk>(HandleStreamChunkAsync);

        // The watched supervisor stopped: without this handler its Terminated would take the manager down with a DeathPactException.
        // The reference is cleared, so the next message brings up a new supervisor instead of reaching dead letters.
        Receive<Terminated>(msg =>
        {
            actorLogger.Warning("Watched actor terminated: {0}; clearing supervisor reference", msg.ActorRef.Path);
            supervisor = null;
        });
    }

    /// <summary>Starts the conversation on the handshake that the controller has already settled and has Morgana present herself.</summary>
    private async Task HandleCreateConversationAsync(Records.CreateConversation msg)
    {
        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        // A repeated start finds its supervisor already there and presents nothing twice.
        if (!await EnsureSupervisorAsync())
            return;

        // The supervisor writes the welcome and delivers it as the first message of the conversation.
        supervisor!.Tell(new Records.GeneratePresentationMessage());
        actorLogger.Info("Presentation generation triggered");
    }

    /// <summary>
    /// Gives the conversation a supervisor when it has none, returning whether one was brought up.
    /// That happens at the start, on the first message to a process that never saw the conversation start and after the supervisor died:
    /// nothing is handed to it, since it takes the conversation's state from the conversation's record.
    /// </summary>
    private async Task<bool> EnsureSupervisorAsync()
    {
        // A supervisor already serving the conversation needs nothing.
        if (supervisor is not null)
            return false;

        // The supervisor is named after the conversation, so one still alive under that name is reused.
        supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
            Constants.Actors.Supervisor, conversationId);

        // The supervisor's death arrives here as a Terminated message, which clears the reference.
        Context.Watch(supervisor);

        actorLogger.Info("Supervisor created: {0}", supervisor.Path);
        return true;
    }

    /// <summary>Ends the conversation here: it stops the supervisor and drops the in-memory copy of the handshake.</summary>
    private Task HandleTerminateConversationAsync(Records.TerminateConversation msg)
    {
        actorLogger.Info($"Terminating conversation {msg.ConversationId}");

        // A conversation ended twice or ended before it ever produced a turn reaches here with no supervisor.
        if (supervisor is not null)
        {
            // Stops the supervisor alone: the guard, the classifier, the router and the agents live flat under the guardian.
            Context.Stop(supervisor);

            // The reference goes at once, so a message arriving before the Terminated brings up a new supervisor instead of reaching dead letters.
            supervisor = null;

            actorLogger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
        }

        // The conversation is over and nothing more goes out: the in-memory copy of its handshake
        // would only be a leak, while the record stays for a later resume.
        channelMetadataStore.EvictChannelMetadata(msg.ConversationId);

        return Task.CompletedTask;
    }

    /// <summary>Hands the user message to the supervisor, bringing one up first when the conversation has none.</summary>
    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        actorLogger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        // Normally the supervisor is already there. It is not when it died or when this process never
        // saw the conversation start, as after a restart: either way the turn does not wait on it.
        await EnsureSupervisorAsync();

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor!.Path);

        // The turn's answer comes back as stream chunks and whole answers, each its own message to this manager.
        supervisor!.Tell(msg);
    }

    /// <summary>
    /// Delivers a streamed piece of the answer to the client.
    /// A chunk only exists for a channel that takes streaming: the agent streams by the capabilities stamped on its request,
    /// which were settled once at the handshake.
    /// </summary>
    private async Task HandleStreamChunkAsync(Records.AgentStreamChunk chunk)
    {
        try
        {
            // The client renders the answer as it grows.
            await channelService.SendStreamChunkAsync(conversationId, chunk.Text);
        }
        catch (Exception ex)
        {
            // A lost chunk costs only the progressive rendering: the whole answer still follows.
            actorLogger.Error(ex, "Failed to send stream chunk to client");
        }
    }

    /// <summary>Records Morgana's own answers, delivers the answer with its dust gauge and follows it with the dust notices due.</summary>
    private async Task HandleConversationResponseAsync(Records.ConversationResponse response)
    {
        actorLogger.Info(
            $"Received response from supervisor (agent: {response.AgentName ?? "unknown"}," +
            $"completed: {response.AgentCompleted}): " +
            $"{response.Response[..Math.Min(50, response.Response.Length)]}...," +
            $"#quickReplies: {response.QuickReplies?.Count ?? 0}" +
            $"#richCard: {response.RichCard != null}");

        // The dust gauge before the send, from 1.0 when full to 0.0 when empty; null when dust limiting is off.
        // Only this message's own adaptation is missing from it, which cannot be known until the adapter has run:
        // the reading after the send supersedes it on the trailing notice.
        ConversationMetadata? preSendMetadata = await dustLimitService.GetRemainingLevelAsync(conversationId) is { } preSendLevel
            ? new ConversationMetadata(preSendLevel)
            : null;

        // One instant for this answer, whether it comes dated by the agent that recorded it or is
        // dated here. The record and the push must agree to the millisecond: a client compares the
        // two to tell a reply it already has from one it missed, so two readings of the clock would
        // make the same answer arrive twice.
        DateTime answerTimestamp = response.RecordedTimestamp ?? DateTime.UtcNow;

        // Two answers can close one turn, an agent's own and then Morgana's farewell, while a client discards anything
        // dated no later than what it already has: sharing an instant would make the second one vanish with no catch-up able to recover it.
        // Only an answer dated here can be nudged, since one an agent recorded keeps the date its own session holds it under.
        if (response.RecordedTimestamp is null && answerTimestamp <= lastAnswerTimestamp)
            answerTimestamp = lastAnswerTimestamp.AddMilliseconds(1);

        // The next answer of the turn is dated after this one.
        lastAnswerTimestamp = answerTimestamp;

        // Undated by an agent means no agent wrote it: a refusal, a disambiguation, an intent nobody handles, a turn that ran out of time.
        // Morgana said it, so it goes on her side of the conversation: otherwise a client rereading the history
        // finds its own question with no answer under it.
        if (response.RecordedTimestamp is null)
            await conversationPersistenceService.AppendOrchestratorMessagesAsync(
                conversationId,
                [new ChatMessage(ChatRole.Assistant, response.Response) { CreatedAt = answerTimestamp }]);

        try
        {
            // Delivers the turn's answer to the user through the adapting channel service, which
            // degrades it to the channel's capabilities before handing it to the concrete transport.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = response.Response,
                MessageType = ChannelMessageTypes.Assistant,
                QuickReplies = response.QuickReplies,
                AgentName = response.AgentName ?? Constants.Morgana,
                AgentCompleted = response.AgentCompleted,
                // Dated as the history keeps it, so a client catching up recognises the reply it was pushed.
                Timestamp = answerTimestamp,
                RichCard = response.RichCard,
                ConversationMetadata = preSendMetadata
            });

            actorLogger.Info(
                $"Response sent successfully to client via channel " +
                $"(#quickReplies: {response.QuickReplies?.Count ?? 0}," +
                $"hasRichCard: {response.RichCard != null})");

            // The gauge again after the send, which may have burnt dust: adapting the answer for a poor channel costs a ChannelAdapter call.
            // This is the end-of-turn level that the dust gate will see on the next message,
            // so the notices below are decided on it.
            ConversationMetadata? postSendMetadata = await dustLimitService.GetRemainingLevelAsync(conversationId) is { } postSendLevel
                ? new ConversationMetadata(postSendLevel)
                : null;

            // The lockout is announced on the turn that drained the budget, so the user is not left to send a doomed message:
            // a level at or below 0.0 is the state the dust gate refuses.
            // It takes precedence over the 70% and 90% warnings, since a spent conversation needs no warning that it runs low.
            if (postSendMetadata is { DustLevel: <= 0.0 })
                await EmitDustExhaustionAsync();
            // A budget still alive may have just crossed a warning threshold.
            else
                await EmitDustWarningsIfNeededAsync(postSendMetadata?.DustLevel ?? 0.0);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send channel message to client");

            // The client is told that its answer was lost; a second failure means it is unreachable and is only logged.
            try
            {
                await channelService.SendMessageAsync(new ChannelMessage
                {
                    ConversationId = conversationId,
                    Text = "An error occurred while sending the response.",
                    MessageType = ChannelMessageTypes.Assistant,
                    ErrorReason = $"delivery_error: {ex.Message}",
                    AgentName = Constants.Morgana,
                    AgentCompleted = false
                });
            }
            catch (Exception fallbackEx)
            {
                actorLogger.Error(fallbackEx, "Failed to send error notification to client");
            }
        }
    }

    /// <summary>
    /// Sends the advisory warning of a dust threshold that the turn has just crossed, never throwing.
    /// The dust service marks each threshold atomically, so a warning is never sent twice.
    /// </summary>
    private async Task EmitDustWarningsIfNeededAsync(double remaining)
    {
        // Nothing to warn about when the budget is not being enforced at all.
        if (!dustLimitingOptions.Enabled)
            return;

        try
        {
            // The limiter reports the thresholds this turn has just crossed and marks them in the same step.
            (bool send70, bool send90) = await dustLimitService.CheckAndMarkWarningsAsync(conversationId);

            // No threshold was crossed on this turn.
            if (!send70 && !send90)
                return;

            // 90% supersedes 70%: when both are crossed in the same turn the user only needs the more urgent message.
            string template = send90 ? dustLimitingOptions.Warning90Message : dustLimitingOptions.Warning70Message;

            // PromptHarness reads this line from the host's log: the warning is a second message of the turn,
            // which its webhook receiver does not observe cleanly.
            actorLogger.Info(string.Create(CultureInfo.InvariantCulture, $"DUST WARNING ({(send90 ? 90 : 70)}%) for {conversationId}, remaining={remaining:F2}"));

            // The warning's percentage and its gauge come from the same end-of-turn reading, so they always agree.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = FormatDustMessage(template, remaining),
                MessageType = ChannelMessageTypes.SystemWarning,
                ErrorReason = send90 ? ChannelErrorReasons.DustBudgetLow90 : ChannelErrorReasons.DustBudgetLow70,
                AgentName = Constants.Morgana,
                AgentCompleted = false,
                ConversationMetadata = new ConversationMetadata(remaining)
            });
        }
        catch (Exception ex)
        {
            // A lost warning is advisory only: the answer has already been delivered.
            actorLogger.Error(ex, "Failed to emit dust budget warning for {0}", conversationId);
        }
    }

    /// <summary>
    /// Sends the lockout notice on the turn that spent the budget, never throwing.
    /// It is identical in text, type and reason to the refusal of the dust gate, so a channel renders and deduplicates the two as one.
    /// </summary>
    private async Task EmitDustExhaustionAsync()
    {
        try
        {
            // PromptHarness reads this line from the host's log, as it does the warnings.
            actorLogger.Info($"DUST EXHAUSTED for {conversationId}");

            // Sends the lockout notice with a gauge pinned at zero, right after the answer this
            // turn already delivered: the conversation stays alive but will accept no further turn.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = dustLimitingOptions.ErrorMessage,
                MessageType = ChannelMessageTypes.Error,
                ErrorReason = ChannelErrorReasons.DustBudgetExhausted,
                AgentName = Constants.Morgana,
                AgentCompleted = false,
                ConversationMetadata = new ConversationMetadata(0.0)
            });
        }
        catch (Exception ex)
        {
            // A lost notice leaves the lockout to the dust gate, which refuses the next message with the same text.
            actorLogger.Error(ex, "Failed to emit dust exhaustion notice for {0}", conversationId);
        }
    }

    /// <summary>Fills a dust template's <c>{percent}</c> with the share of the budget still available, from 0 to 100.</summary>
    private static string FormatDustMessage(string template, double remaining)
    {
        // Truncated toward zero, so a residual under 1% reads as 0% rather than rounding up past an exhaustion the turn already overran.
        int percent = (int)(Math.Clamp(remaining, 0.0, 1.0) * 100);

        // The template is authored in configuration, with the placeholder where the percentage goes.
        return template.Replace("{percent}", percent.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Logs the manager's start; the conversation itself starts only when CreateConversation arrives.</summary>
    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");

        base.PreStart();
    }

    /// <summary>Drops the in-memory copy of the handshake on any stop, a supervision failure and a system shutdown included.</summary>
    protected override void PostStop()
    {
        // A stop that never went through the conversation's end would otherwise leave the copy behind.
        channelMetadataStore.EvictChannelMetadata(conversationId);

        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");

        base.PostStop();
    }
}