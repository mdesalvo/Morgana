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
/// Entry point actor for managing conversations: the primary interface between the external
/// system and the internal actor hierarchy, owning conversation lifecycle (creation/termination)
/// and forwarding user messages to <see cref="ConversationSupervisorActor"/>.
/// </summary>
public class ConversationManagerActor : MorganaActor
{
    /// <summary>
    /// Outbound channel used to deliver messages from the actor system to the end user.
    /// Abstracts the transport + client pair (e.g. SignalR + Cauldron web UI) so the actor
    /// does not depend on any specific delivery mechanism. Also exposes
    /// <see cref="ChannelCapabilities"/> so producers can degrade features
    /// (rich cards, streaming, quick replies) when the target channel does not support them.
    /// </summary>
    private readonly IChannelService channelService;

    /// <summary>
    /// The conversation's channel, settled at the handshake before this actor exists. This actor
    /// only drops the in-memory copy when the conversation stops being served here.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// Per-conversation lifetime token-budget limiter. Read after each turn to stamp the
    /// remaining dust level on the outbound message and to emit one-shot 70%/90% warnings.
    /// </summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>
    /// Dust-limiting policy (budget + warning message templates) for placeholder substitution.
    /// </summary>
    private readonly Records.DustLimitingOptions dustLimitingOptions;

    /// <summary>
    /// Keeps Morgana's own side of the conversation: every answer that passes through here without
    /// an agent having recorded it is hers. A transcript that lacked it would show the user a
    /// question of theirs with nothing under it.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

    /// <summary>
    /// The instant the last answer went out under. Kept so a turn closing with two answers dates
    /// them apart: a client takes anything dated no later than what it holds as a repeat.
    /// </summary>
    private DateTime lastAnswerTimestamp = DateTime.MinValue;

    /// <summary>
    /// Reference to the conversation supervisor actor. Null until the conversation is started here or
    /// its first message reaches this process. The supervisor dying clears it again.
    /// </summary>
    private IActorRef? supervisor;

    /// <summary>
    /// Initializes a new instance of the ConversationManagerActor.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="channelService">Channel service used to deliver outbound messages to the end user</param>
    /// <param name="channelMetadataStore">The conversation's channel, whose in-memory copy this actor releases</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="conversationPersistenceService">Keeps Morgana's own answers on the conversation's record</param>
    /// <param name="dustLimitingOptions">Dust-limiting policy and warning message templates</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    public ConversationManagerActor(
        string conversationId,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IDustLimitService dustLimitService,
        IConversationPersistenceService conversationPersistenceService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.dustLimitService = dustLimitService;
        this.conversationPersistenceService = conversationPersistenceService;
        this.dustLimitingOptions = dustLimitingOptions.Value;

        // Handle incoming user messages:
        // - Ensures supervisor exists (creates if missing)
        // - Forwards message to supervisor using Tell to support streaming
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        // Handle conversation lifecycle requests:
        // - CreateConversation: creates supervisor actor, triggers the presentation of a new conversation
        // - TerminateConversation: stops supervisor actor and clears reference
        ReceiveAsync<Records.CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<Records.TerminateConversation>(HandleTerminateConversationAsync);
        ReceiveAsync<Records.ConversationResponse>(HandleConversationResponseAsync);

        // Handle supervisor responses:
        // - ConversationResponse: final response from supervisor → send to client via SignalR
        ReceiveAsync<Records.AgentStreamChunk>(HandleStreamChunkAsync);

        // Handle termination of watched actors (supervisor).
        // Without this handler Akka throws DeathPactException when the supervisor stops,
        // because the default Unhandled path re-throws Terminated as a fatal exception.
        // Clear the supervisor reference so the next UserMessage doesn't forward to dead letters.
        Receive<Terminated>(msg =>
        {
            actorLogger.Warning("Watched actor terminated: {0}; clearing supervisor reference", msg.ActorRef.Path);
            supervisor = null;
        });
    }

    /// <summary>
    /// Handles the start of a new conversation, whose handshake the controller has already settled:
    /// creates the supervisor and has it present Morgana to the user.
    /// </summary>
    /// <param name="msg">Conversation creation request message</param>
    private async Task HandleCreateConversationAsync(Records.CreateConversation msg)
    {
        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        // A repeated start finds its supervisor already there and presents nothing twice
        if (!await EnsureSupervisorAsync())
            return;

        // Asks the supervisor for the welcome message and its quick replies, which travel back
        // through the ordinary outbound path as the first message of the conversation.
        supervisor!.Tell(new Records.GeneratePresentationMessage());
        actorLogger.Info("Presentation generation triggered");
    }

    /// <summary>
    /// Gives the conversation a supervisor when it has none: at the start, on the first message a
    /// process receives for a conversation it never saw start, after the supervisor died. Nothing
    /// is handed to it: it takes the conversation's state from the conversation's record.
    /// </summary>
    /// <returns>True when a supervisor was created, false when one was already there.</returns>
    private async Task<bool> EnsureSupervisorAsync()
    {
        if (supervisor is not null)
            return false;

        // The FSM orchestrator of the turn pipeline, named after this conversation
        // (/user/supervisor-{conversationId}), reusing it if the actor path already exists.
        supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
            Constants.Actors.Supervisor, conversationId);

        // Watch the supervisor so its death arrives here as a Terminated message
        // (handled above) instead of taking the manager down with a DeathPactException.
        Context.Watch(supervisor);

        actorLogger.Info("Supervisor created: {0}", supervisor.Path);
        return true;
    }

    /// <summary>
    /// Handles conversation termination requests.
    /// Stops the supervisor actor and clears the reference.
    /// </summary>
    /// <param name="msg">Conversation termination request message</param>
    private Task HandleTerminateConversationAsync(Records.TerminateConversation msg)
    {
        actorLogger.Info($"Terminating conversation {msg.ConversationId}");

        // Check whether there is anything to tear down: a conversation ended twice, or ended before
        // it ever produced a turn, reaches here with no supervisor.
        if (supervisor is not null)
        {
            // Stops the supervisor and, with it, the whole child subtree (guard, classifier,
            // router and the agents underneath).
            Context.Stop(supervisor);

            // Drops the reference immediately rather than waiting for the Terminated message,
            // so no message forwarded in between lands in dead letters.
            supervisor = null;

            actorLogger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
        }

        // The conversation is over and nothing more goes out: the in-memory copy of its handshake
        // would only be a leak, while the record stays for a later resume.
        channelMetadataStore.EvictChannelMetadata(msg.ConversationId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles incoming user messages.
    /// Ensures supervisor exists, then forwards the message using Tell to support streaming.
    /// </summary>
    /// <param name="msg">User message to process</param>
    /// <remarks>
    /// Uses Tell pattern to support streaming chunks and final response separately.
    /// </remarks>
    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        actorLogger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        // Normally the supervisor is already there. It is not when it died or when this process never
        // saw the conversation start, as after a restart: either way the turn does not wait on it.
        await EnsureSupervisorAsync();

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor!.Path);

        // Hands the turn to the supervisor with Tell rather than Ask: the answer comes back
        // asynchronously as stream chunks plus a final ConversationResponse, not as a single reply.
        supervisor!.Tell(msg);
    }

    /// <summary>
    /// Handles streaming chunks from the supervisor and forwards them to the client via the active channel.
    /// Enables real-time progressive response rendering in the UI.
    /// </summary>
    /// <param name="chunk">Streaming chunk containing partial response text</param>
    /// <remarks>
    /// A chunk only exists for a channel that takes streaming: the agent streams by the capabilities
    /// stamped on its request, which were settled once at the handshake (see NormaliseCapabilities).
    /// </remarks>
    private async Task HandleStreamChunkAsync(Records.AgentStreamChunk chunk)
    {
        try
        {
            // Forward chunk to client via the active channel for progressive rendering
            await channelService.SendStreamChunkAsync(conversationId, chunk.Text);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send stream chunk to client");
        }
    }

    /// <summary>
    /// Handles final response from supervisor (direct Tell, not PipeTo wrapper).
    /// Sends the response to the client via SignalR with appropriate metadata.
    /// </summary>
    /// <param name="response">ConversationResponse from supervisor</param>
    private async Task HandleConversationResponseAsync(Records.ConversationResponse response)
    {
        actorLogger.Info(
            $"Received response from supervisor (agent: {response.AgentName ?? "unknown"}," +
            $"completed: {response.AgentCompleted}): " +
            $"{response.Response[..Math.Min(50, response.Response.Length)]}...," +
            $"#quickReplies: {response.QuickReplies?.Count ?? 0}" +
            $"#richCard: {response.RichCard != null}");

        // Reads the dust gauge before sending: the fraction still available (1.0 = full,
        // 0.0 = empty). Every previous turn is already accounted for; the only cost missing is
        // this message's own adaptation, which cannot be known until the adapter has run. So the
        // value rides on the main response as a best effort and the reading taken after the send
        // (below) supersedes it on the trailing warning message. Null when dust limiting is off.
        ConversationMetadata? preSendMetadata = await dustLimitService.GetRemainingLevelAsync(conversationId) is { } preSendLevel
            ? new ConversationMetadata(preSendLevel)
            : null;

        // One instant for this answer, whether it comes dated by the agent that recorded it or is
        // dated here. The record and the push must agree to the millisecond: a client compares the
        // two to tell a reply it already has from one it missed, so two readings of the clock would
        // make the same answer arrive twice.
        DateTime answerTimestamp = response.RecordedTimestamp ?? DateTime.UtcNow;

        // Two answers can now close one turn (an agent's own, then Morgana taking the conversation
        // back) while a client discards anything dated no later than what it already has. Sharing an
        // instant with the answer it follows would make the second one vanish on the way out, with
        // no trace anywhere and no catch-up able to recover it. Only an answer dated here can be
        // nudged: one an agent recorded must keep the date its own session holds it under.
        if (response.RecordedTimestamp is null && answerTimestamp <= lastAnswerTimestamp)
            answerTimestamp = lastAnswerTimestamp.AddMilliseconds(1);

        lastAnswerTimestamp = answerTimestamp;

        // Undated by an agent means no agent wrote it: a refusal, a disambiguation, an intent
        // nobody handles, a turn that ran out of time. Morgana said it, so it goes on her side of
        // the conversation — otherwise a client rereading the history finds its own question with
        // no answer under it and has to invent one, which is what every channel used to do.
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
                MessageType = Constants.MessageTypes.Assistant,
                QuickReplies = response.QuickReplies,
                AgentName = response.AgentName ?? Constants.Morgana,
                AgentCompleted = response.AgentCompleted,
                // Dated as the history keeps it, so a client catching up recognises the reply it was pushed
                Timestamp = answerTimestamp,
                RichCard = response.RichCard,
                ConversationMetadata = preSendMetadata
            });

            actorLogger.Info(
                $"Response sent successfully to client via channel " +
                $"(#quickReplies: {response.QuickReplies?.Count ?? 0}," +
                $"hasRichCard: {response.RichCard != null})");

            // Reads the gauge again after the send, because the send itself may have burnt dust:
            // degrading the response for a poor channel (Rune squeezing a long answer into its
            // 500-char profile) costs a ChannelAdapter LLM call. This second reading is the
            // authoritative end-of-turn level — the same number IsOverBudgetAsync will see on the
            // next send — so the warning/exhaustion decision below and the gauge the trailing
            // message carries, are taken on it rather than on the stale pre-send snapshot.
            ConversationMetadata? postSendMetadata = await dustLimitService.GetRemainingLevelAsync(conversationId) is { } postSendLevel
                ? new ConversationMetadata(postSendLevel)
                : null;

            // Announces the lockout on the very turn that drained the budget, delivery included,
            // rather than letting the user send a doomed next message and collect an instant 429:
            // a DustLevel at or below 0.0 is exactly the over-budget state the controller gate
            // rejects. It takes precedence over the advisory 70% / 90% warnings, since a
            // conversation already dead does not need to be told it is running low.
            if (postSendMetadata is { DustLevel: <= 0.0 })
                await EmitDustExhaustionAsync();
            else
                await EmitDustWarningsIfNeededAsync(postSendMetadata?.DustLevel ?? 0.0);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send channel message to client");

            // A second channel-level failure here means the client is genuinely unreachable —
            // logged and left at that, since there's no further fallback delivery path to try.
            try
            {
                await channelService.SendMessageAsync(new ChannelMessage
                {
                    ConversationId = conversationId,
                    Text = "An error occurred while sending the response.",
                    MessageType = "assistant",
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
    /// Checks the dust-budget warning thresholds and, for any threshold newly crossed,
    /// emits a one-shot advisory <c>system_warning</c> message. The 70% / 90% one-shot
    /// flags are owned and atomically marked by <see cref="IDustLimitService"/>, so this
    /// never re-sends the same warning. Best-effort: failures are logged, never thrown.
    /// </summary>
    private async Task EmitDustWarningsIfNeededAsync(double remaining)
    {
        // Nothing to warn about when the budget is not being enforced at all.
        if (!dustLimitingOptions.Enabled)
            return;

        try
        {
            // Asks the limiter which thresholds this turn has just crossed; the call also marks
            // them atomically, so each warning is claimed once and never sent twice.
            (bool send70, bool send90) = await dustLimitService.CheckAndMarkWarningsAsync(conversationId);
            if (!send70 && !send90)
                return;

            // 90% supersedes 70%: if both crossed in the same turn, the user only needs
            // the more urgent message.
            string template = send90 ? dustLimitingOptions.Warning90Message : dustLimitingOptions.Warning70Message;

            // Diagnostic only, no behavioural effect: PromptHarness taps the host's log output the
            // same way it does for MorganaChatReducer's reduction line, since the wire message this
            // emits is a second, out-of-band ChannelMessage the harness's single-message-per-turn
            // webhook receiver does not otherwise observe cleanly.
            actorLogger.Info(string.Create(CultureInfo.InvariantCulture, $"DUST WARNING ({(send90 ? 90 : 70)}%) for {conversationId}, remaining={remaining:F2}"));

            // Use the identical `remaining` value from the main response's ConversationMetadata
            // so the warning text percentage and the gauge are always in sync.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = FormatDustMessage(template, remaining),
                MessageType = Constants.MessageTypes.SystemWarning,
                ErrorReason = send90 ? "dust_budget_low_90" : "dust_budget_low_70",
                AgentName = Constants.Morgana,
                AgentCompleted = false,
                ConversationMetadata = new ConversationMetadata(remaining)
            });
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to emit dust budget warning for {0}", conversationId);
        }
    }

    /// <summary>
    /// Pushes the terminal dust-exhaustion notice at end of turn, when the budget has
    /// just been spent. Deliberately identical (text, <c>MessageType</c>,
    /// <c>ErrorReason</c>) to the banner the message endpoint emits on a doomed next
    /// send, so a channel that already renders the lockout (Cauldron's non-fading
    /// terminal banner) and its de-dup keep working unchanged. Best-effort: a delivery
    /// failure is logged, never thrown — the conversation is already over.
    /// </summary>
    private async Task EmitDustExhaustionAsync()
    {
        try
        {
            // Diagnostic only, no behavioural effect — see EmitDustWarningsIfNeededAsync's own
            // remark on why PromptHarness needs this tapped from the log rather than the wire.
            actorLogger.Info($"DUST EXHAUSTED for {conversationId}");

            // Sends the lockout notice with a gauge pinned at zero, right after the answer this
            // turn already delivered: the conversation stays alive but will accept no further turn.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = dustLimitingOptions.ErrorMessage,
                MessageType = Constants.MessageTypes.Error,
                ErrorReason = Constants.ErrorReasons.DustBudgetExhausted,
                AgentName = Constants.Morgana,
                AgentCompleted = false,
                ConversationMetadata = new ConversationMetadata(0.0)
            });
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to emit dust exhaustion notice for {0}", conversationId);
        }
    }

    /// <summary>
    /// Renders <paramref name="remaining"/> as the 0–100 <c>{percent}</c> a warning/exhaustion
    /// template shows — fuel-gauge semantics users reason in, not abstract dust units. Truncated
    /// toward zero rather than rounded, so a sub-1% residual reads as 0% instead of misleadingly
    /// rounding up past the exhaustion the let-it-finish policy already let this turn overrun.
    /// </summary>
    private static string FormatDustMessage(string template, double remaining)
    {
        int percent = (int)(Math.Clamp(remaining, 0.0, 1.0) * 100);
        return template.Replace("{percent}", percent.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Logs actor startup; conversation setup itself only happens once CreateConversation arrives.</summary>
    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");

        // Lets the base actor run its own startup after the logging.
        base.PreStart();
    }

    /// <summary>
    /// Evicts the in-memory copy of this conversation's handshake so a stop that skips
    /// <see cref="HandleTerminateConversationAsync"/> (a supervision failure, a system shutdown)
    /// can't leave it behind in <see cref="IChannelMetadataStore"/>.
    /// </summary>
    protected override void PostStop()
    {
        // Drops the copy on any stop, including the ones that never went through
        // HandleTerminateConversationAsync, where the eviction would otherwise be missed.
        channelMetadataStore.EvictChannelMetadata(conversationId);

        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");

        // Lets the base actor run its own teardown after the cleanup.
        base.PostStop();
    }
}