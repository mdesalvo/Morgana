using Akka.Actor;
using Akka.Event;
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
    /// In-process registry where this actor publishes the per-conversation channel metadata,
    /// so the <c>AdaptingChannelService</c> decorator can degrade outbound messages on every
    /// send and <c>ConversationSupervisorActor</c> can stamp the capabilities on per-turn
    /// agent requests.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// Persistence service used to save the channel metadata at conversation start
    /// (handshake) and to load it on restore.
    /// </summary>
    private readonly IConversationPersistenceService conversationPersistenceService;

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
    /// Reference to the active conversation supervisor actor.
    /// Null until a conversation is created.
    /// </summary>
    private IActorRef? supervisor;

    /// <summary>
    /// Initializes a new instance of the ConversationManagerActor.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="channelService">Channel service used to deliver outbound messages to the end user</param>
    /// <param name="channelMetadataStore">Registry where this actor publishes the per-conversation channel metadata</param>
    /// <param name="conversationPersistenceService">Persistence service used to save/load the channel handshake</param>
    /// <param name="dustLimitService">Per-conversation lifetime token-budget limiter</param>
    /// <param name="dustLimitingOptions">Dust-limiting policy and warning message templates</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    public ConversationManagerActor(
        string conversationId,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IConversationPersistenceService conversationPersistenceService,
        IDustLimitService dustLimitService,
        IOptions<Records.DustLimitingOptions> dustLimitingOptions,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.conversationPersistenceService = conversationPersistenceService;
        this.dustLimitService = dustLimitService;
        this.dustLimitingOptions = dustLimitingOptions.Value;

        // Handle incoming user messages:
        // - Ensures supervisor exists (creates if missing)
        // - Forwards message to supervisor using Tell to support streaming
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        // Handle conversation lifecycle requests:
        // - CreateConversation: creates supervisor actor, triggers automatic presentation generation
        // - TerminateConversation: stops supervisor actor and clears reference
        // - RestoreActiveAgent: forwards the restore request to the supervisor (used on resume)
        ReceiveAsync<Records.CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<Records.TerminateConversation>(HandleTerminateConversationAsync);
        Receive<Records.RestoreActiveAgent>(HandleRestoreActiveAgent);
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
    /// Handles conversation creation requests.
    /// Creates and watches the supervisor actor, then triggers automatic presentation generation.
    /// </summary>
    /// <param name="msg">Conversation creation request message</param>
    private async Task HandleCreateConversationAsync(Records.CreateConversation msg)
    {
        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        // Check if the supervisor has already been created for this conversation: the manager owns
        // exactly one, so a repeated create (or a resume landing on a live manager) is a no-op.
        if (supervisor is null)
        {
            // Resolve and publish the per-conversation channel metadata BEFORE creating the
            // supervisor, so any outbound message produced by the supervisor (including the
            // presentation on a fresh start) is already covered by the registered entry.
            ChannelMetadata effectiveMetadata = await ResolveChannelMetadataAsync(msg);
            channelMetadataStore.RegisterChannelMetadata(msg.ConversationId, effectiveMetadata);
            actorLogger.Info(
                $"Channel metadata registered for {msg.ConversationId}: " +
                $"channel={effectiveMetadata.Coordinates.ChannelName}, " +
                $"delivery={effectiveMetadata.Coordinates.DeliveryMode}, " +
                $"rc={effectiveMetadata.Capabilities.SupportsRichCards}, " +
                $"qr={effectiveMetadata.Capabilities.SupportsQuickReplies}, " +
                $"str={effectiveMetadata.Capabilities.SupportsStreaming}, " +
                $"md={effectiveMetadata.Capabilities.SupportsMarkdown}, " +
                $"max={effectiveMetadata.Capabilities.MaxMessageLength}");

            // Create the FSM orchestrator of the turn pipeline, named after this conversation
            // (/user/supervisor-{conversationId}), reusing it if the actor path already exists.
            supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            // Watch the supervisor so its death arrives here as a Terminated message
            // (handled above) instead of taking the manager down with a DeathPactException.
            Context.Watch(supervisor);

            actorLogger.Info("Supervisor created: {0}", supervisor.Path);

            // Trigger automatic presentation (only in case of new conversation)
            if (!msg.IsRestore)
            {
                // Asks the supervisor for the welcome message and its quick replies, which travel
                // back through the ordinary outbound path as the first message of the conversation.
                supervisor.Tell(new Records.GeneratePresentationMessage());

                actorLogger.Info("Presentation generation triggered");
            }
        }
    }

    /// <summary>
    /// Resolves channel metadata: fresh start persists metadata from controller gate (lowercased);
    /// restore loads from DB. No fallback on restore — pre-handshake conversations or lost rows
    /// are refused to prevent inventing an identity.
    /// </summary>
    private async Task<ChannelMetadata> ResolveChannelMetadataAsync(Records.CreateConversation msg)
    {
        // Split the two provenances of the metadata: a fresh start carries the handshake declared
        // by the client, a resume has nothing to carry and must read back what was persisted.
        if (!msg.IsRestore)
        {
            // Fresh start: the controller has already gated the request and guarantees
            // ChannelMetadata is present (Morgana refuses handshakes from channels that do
            // not announce themselves). A null here would be an internal bug, not a client
            // mistake — fail loudly so the regression surfaces immediately.
            if (msg.ChannelMetadata is null)
                throw new InvalidOperationException(
                    $"Fresh conversation {msg.ConversationId} reached the manager without channel metadata; " +
                    "the start-conversation gate in MorganaController should have rejected this.");

            // Normalise the declaration at the ingress so every downstream consumer sees
            // consistent data: ChannelName and DeliveryMode are trimmed and lowercased so their
            // name spaces stay case-insensitive end-to-end, and Capabilities are reconciled
            // against the AdaptiveMessaging policy (see NormaliseCapabilities) before being
            // persisted and registered.
            ChannelMetadata channelMetadata = new ChannelMetadata
            {
                Coordinates = msg.ChannelMetadata.Coordinates with
                {
                    ChannelName = msg.ChannelMetadata.Coordinates.ChannelName.Trim().ToLowerInvariant(),
                    DeliveryMode = msg.ChannelMetadata.Coordinates.DeliveryMode.Trim().ToLowerInvariant()
                },
                Capabilities = NormaliseCapabilities(msg.ChannelMetadata.Capabilities)
            };

            try
            {
                // Persist the normalised handshake in the conversation DB so a later resume can
                // recover the channel identity; a failure here is logged and swallowed, since the
                // in-memory registration below keeps the current lifetime fully functional.
                await conversationPersistenceService.SaveChannelMetadataAsync(msg.ConversationId, channelMetadata);
            }
            catch (Exception ex)
            {
                actorLogger.Error(ex, "Failed to persist channel metadata for {0}; in-memory entry will still be registered", msg.ConversationId);
            }

            return channelMetadata;
        }

        // Restore path: metadata must have been announced and persisted in a previous
        // lifetime of the conversation. No fallback to the transport's self-advertised
        // identity — that would reintroduce the transport≡channel coupling we just removed.
        ChannelMetadata? restoredChannelMetadata = await conversationPersistenceService.LoadChannelMetadataAsync(msg.ConversationId);
        if (restoredChannelMetadata is null)
            throw new InvalidOperationException(
                $"Restore requested for conversation {msg.ConversationId} but no channel metadata is persisted; " +
                "Morgana refuses to invent a channel identity for a conversation whose origin is unknown.");

        return restoredChannelMetadata;
    }

    /// <summary>
    /// Normalises incoherent ChannelCapabilities at ingress. Channels with MaxMessageLength
    /// below RichFeaturesMinLength threshold are treated as primitive (clear rich cards/quick replies).
    /// Streaming is untouched — transport property orthogonal to length cap. Null/non-positive
    /// threshold disables heuristic and restores full trust of declarations.
    /// </summary>
    private ChannelCapabilities NormaliseCapabilities(ChannelCapabilities declaredCapabilities)
    {
        // A channel declaring no length cap has nothing to be judged primitive by: its
        // declaration is taken at face value and returned untouched.
        if (declaredCapabilities.MaxMessageLength is not { } max)
            return declaredCapabilities;

        // Read the configured minimum length a channel must afford for rich features to be believable.
        int threshold = configuration.GetValue<int>("Morgana:AdaptiveMessaging:RichFeaturesMinLength", 0);
        if (threshold <= 0 || max >= threshold)
            return declaredCapabilities;

        return declaredCapabilities with
        {
            SupportsRichCards = false,
            SupportsQuickReplies = false
        };
    }

    /// <summary>
    /// Forwards a <see cref="Records.RestoreActiveAgent"/> request to the supervisor.
    /// Routing through the manager (instead of having the controller create the supervisor
    /// directly) guarantees ordering: the preceding <see cref="Records.CreateConversation"/>
    /// is drained from this mailbox first, so the supervisor and the channel metadata are
    /// always registered before the restore request reaches it — no race between two parallel
    /// <c>ActorOf("supervisor-...")</c> calls.
    /// </summary>
    private void HandleRestoreActiveAgent(Records.RestoreActiveAgent msg)
    {
        // Guard against a restore arriving without a supervisor to hand it to: the ordering
        // guaranteed by this mailbox makes it a bug rather than a race, so it is logged and dropped
        // instead of creating a supervisor here.
        if (supervisor is null)
        {
            actorLogger.Warning("RestoreActiveAgent received but supervisor is not yet created for {0}; dropping request", conversationId);
            return;
        }

        // Hands the restore over to the supervisor, the only actor that owns the activeAgent slot:
        // once it is set, the next user message skips classification and goes straight to that agent.
        actorLogger.Info("Forwarding RestoreActiveAgent(intent={0}) to supervisor", msg.AgentIntent);
        supervisor.Tell(msg);
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

        // Removes the channel metadata from the in-process registry: the conversation is over and
        // nothing more will be sent out, so the entry would only be a leak.
        channelMetadataStore.UnregisterChannelMetadata(msg.ConversationId);

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

        // Check whether the supervisor is there to receive the turn: normally it was created at
        // conversation start, so its absence means it died (Terminated cleared the reference) and
        // must be recreated here rather than losing the message.
        if (supervisor == null)
        {
            // Recreate the FSM orchestrator under the same conversation-scoped path.
            supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            // Watch the new instance too, so a further death is again seen as Terminated
            // instead of a DeathPactException.
            Context.Watch(supervisor);

            actorLogger.Warning("Supervisor was missing; created new supervisor: {0}", supervisor.Path);
        }

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor.Path);

        // Hands the turn to the supervisor with Tell rather than Ask: the answer comes back
        // asynchronously as stream chunks plus a final ConversationResponse, not as a single reply.
        supervisor.Tell(msg);
    }

    /// <summary>
    /// Handles streaming chunks from the supervisor and forwards them to the client via the active channel.
    /// Enables real-time progressive response rendering in the UI.
    /// </summary>
    /// <param name="chunk">Streaming chunk containing partial response text</param>
    /// <remarks>
    /// Chunks are suppressed entirely when the active channel does not advertise
    /// <see cref="ChannelCapabilities.SupportsStreaming"/>. The final complete message
    /// still reaches the client via <see cref="HandleConversationResponseAsync"/>, so no content
    /// is lost — only the progressive rendering effect is skipped.
    /// </remarks>
    private async Task HandleStreamChunkAsync(Records.AgentStreamChunk chunk)
    {
        // Skip streaming entirely on channels that don't support it.
        // The final response is delivered as a single structured message by HandleConversationResponseAsync.
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? registeredMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any stream chunk.");

        // No way to proceed if streaming is unsupported by the channel
        if (!registeredMetadata.Capabilities.SupportsStreaming)
            return;

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
        // value rides on the main response as a best effort, and the reading taken after the send
        // (below) supersedes it on the trailing warning message. Null when dust limiting is off.
        ConversationMetadata? preSendMetadata = dustLimitingOptions.Enabled
            ? new ConversationMetadata(
                Math.Floor(Math.Clamp(1.0 - await dustLimitService.GetUsageRatioAsync(conversationId), 0.0, 1.0) * 100.0) / 100.0)
            : null;

        try
        {
            // Delivers the turn's answer to the user through the adapting channel service, which
            // degrades it to the channel's capabilities before handing it to the concrete transport.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = response.Response,
                MessageType = "assistant",
                QuickReplies = response.QuickReplies,
                AgentName = response.AgentName ?? "Morgana",
                AgentCompleted = response.AgentCompleted,
                Timestamp = response.OriginalTimestamp ?? DateTime.UtcNow,
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
            // next send — so the warning/exhaustion decision below, and the gauge the trailing
            // message carries, are taken on it rather than on the stale pre-send snapshot.
            ConversationMetadata? postSendMetadata = dustLimitingOptions.Enabled
                ? new ConversationMetadata(
                    Math.Floor(Math.Clamp(1.0 - await dustLimitService.GetUsageRatioAsync(conversationId), 0.0, 1.0) * 100.0) / 100.0)
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
                    AgentName = "Morgana",
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
            actorLogger.Info($"DUST WARNING ({(send90 ? 90 : 70)}%) for {conversationId}, remaining={remaining:F2}");

            // Use the identical `remaining` value from the main response's ConversationMetadata
            // so the warning text percentage and the gauge are always in sync.
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = FormatDustMessage(template, remaining),
                MessageType = "system_warning",
                ErrorReason = send90 ? "dust_budget_low_90" : "dust_budget_low_70",
                AgentName = "Morgana",
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
                MessageType = "error",
                ErrorReason = "dust_budget_exhausted",
                AgentName = "Morgana",
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
        return template.Replace("{percent}", percent.ToString());
    }

    /// <summary>Logs actor startup; conversation setup itself only happens once CreateConversation arrives.</summary>
    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");

        // Lets the base actor run its own startup after the logging.
        base.PreStart();
    }

    /// <summary>
    /// Deregisters this conversation's channel metadata so a stop that skips
    /// <see cref="HandleTerminateConversationAsync"/> (a supervision failure, a system shutdown)
    /// can't leave a stale entry behind in <see cref="IChannelMetadataStore"/>.
    /// </summary>
    protected override void PostStop()
    {
        // Drops the registry entry on any stop, including the ones that never went through
        // HandleTerminateConversationAsync, where the unregistration would otherwise be missed.
        channelMetadataStore.UnregisterChannelMetadata(conversationId);

        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");

        // Lets the base actor run its own teardown after the cleanup.
        base.PostStop();
    }
}