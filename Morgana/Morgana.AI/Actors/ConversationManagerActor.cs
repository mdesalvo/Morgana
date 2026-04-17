using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Entry point actor for managing conversations.
/// Responsible for conversation lifecycle (creation/termination) and message routing to the supervisor.
/// Uses PipeTo pattern for non-blocking communication with the supervisor.
/// </summary>
/// <remarks>
/// This actor serves as the primary interface between the external system (SignalR) and the internal actor hierarchy.
/// It maintains a reference to the ConversationSupervisorActor and forwards user messages for processing.
/// </remarks>
public class ConversationManagerActor : MorganaActor
{
    /// <summary>
    /// Outbound channel used to deliver messages from the actor system to the end user.
    /// Abstracts the transport + client pair (e.g. SignalR + Cauldron web UI) so the actor
    /// does not depend on any specific delivery mechanism. Also exposes
    /// <see cref="Records.ChannelCapabilities"/> so producers can degrade features
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
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    public ConversationManagerActor(
        string conversationId,
        IChannelService channelService,
        IChannelMetadataStore channelMetadataStore,
        IConversationPersistenceService conversationPersistenceService,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.channelService = channelService;
        this.channelMetadataStore = channelMetadataStore;
        this.conversationPersistenceService = conversationPersistenceService;

        // Handle incoming user messages from SignalR:
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

        // Handle supervisor responses (direct Tell, not PipeTo):
        // - ConversationResponse: final response from supervisor → send to client via SignalR
        ReceiveAsync<Records.ConversationResponse>(HandleConversationResponseAsync);

        // Handle streaming chunks from supervisor and forward to client via SignalR
        ReceiveAsync<Records.AgentStreamChunk>(HandleStreamChunkAsync);
    }

    /// <summary>
    /// Handles conversation creation requests.
    /// Creates and watches the supervisor actor, then triggers automatic presentation generation.
    /// </summary>
    /// <param name="msg">Conversation creation request message</param>
    private async Task HandleCreateConversationAsync(Records.CreateConversation msg)
    {
        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        if (supervisor is null)
        {
            // Resolve and publish the per-conversation channel metadata BEFORE creating the
            // supervisor, so any outbound message produced by the supervisor (including the
            // presentation on a fresh start) is already covered by the registered entry.
            Records.ChannelMetadata effectiveMetadata = await ResolveChannelMetadataAsync(msg);
            channelMetadataStore.RegisterChannelMetadata(msg.ConversationId, effectiveMetadata);
            actorLogger.Info(
                $"Channel metadata registered for {msg.ConversationId}: " +
                $"channel={effectiveMetadata.ChannelName}, " +
                $"rc={effectiveMetadata.Capabilities.SupportsRichCards}, " +
                $"qr={effectiveMetadata.Capabilities.SupportsQuickReplies}, " +
                $"str={effectiveMetadata.Capabilities.SupportsStreaming}, " +
                $"md={effectiveMetadata.Capabilities.SupportsMarkdown}, " +
                $"max={effectiveMetadata.Capabilities.MaxMessageLength}");

            supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);

            actorLogger.Info("Supervisor created: {0}", supervisor.Path);

            // Trigger automatic presentation (only in case of new conversation)
            if (!msg.IsRestore)
            {
                supervisor.Tell(new Records.GeneratePresentationMessage());

                actorLogger.Info("Presentation generation triggered");
            }
        }
    }

    /// <summary>
    /// Determines the effective channel metadata for the conversation:
    /// <list type="bullet">
    /// <item>Fresh start → the controller gate guarantees metadata is present; persist it
    /// (lowercased channel name) and return it.</item>
    /// <item>Restore → the <c>ConversationExists</c> gate guarantees the DB exists; load and
    /// return the persisted metadata. No fallback: a restore that reaches here without a
    /// persisted row is a conversation that predates the channel handshake (or whose row was
    /// lost) and we refuse to invent an identity on its behalf.</item>
    /// </list>
    /// </summary>
    private async Task<Records.ChannelMetadata> ResolveChannelMetadataAsync(Records.CreateConversation msg)
    {
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
            // consistent data: ChannelName is lowercased so the name space stays
            // case-insensitive end-to-end, and Capabilities are reconciled against the
            // AdaptiveMessaging policy (see NormaliseCapabilities) before being persisted
            // and registered.
            Records.ChannelMetadata channelMetadata = msg.ChannelMetadata with
            {
                ChannelName = msg.ChannelMetadata.ChannelName.ToLowerInvariant(),
                Capabilities = NormaliseCapabilities(msg.ChannelMetadata.Capabilities)
            };

            try
            {
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
        Records.ChannelMetadata? restoredChannelMetadata = await conversationPersistenceService.LoadChannelMetadataAsync(msg.ConversationId);
        if (restoredChannelMetadata is null)
            throw new InvalidOperationException(
                $"Restore requested for conversation {msg.ConversationId} but no channel metadata is persisted; " +
                "Morgana refuses to invent a channel identity for a conversation whose origin is unknown.");

        return restoredChannelMetadata;
    }

    /// <summary>
    /// Normalises incoming <see cref="Records.ChannelCapabilities"/> so clearly incoherent
    /// declarations are reconciled at the ingress, not carried all the way to the adapter.
    /// A channel whose hard per-message cap falls below
    /// <c>Morgana:AdaptiveMessaging:RichFeaturesMinLength</c> is treated as primitive:
    /// rich cards and quick replies cannot fit inside such a small budget alongside the
    /// textual body, so the flags are cleared regardless of what the client announced.
    /// Streaming is intentionally not touched here — it is a property of the transport
    /// (connection-oriented vs store-and-forward), orthogonal to the length cap. A channel
    /// that advertises streaming with a small cap is trusted until the client corrects it.
    /// Setting the threshold to <c>null</c> or a non-positive value disables the heuristic
    /// and restores full trust of the declaration.
    /// </summary>
    private Records.ChannelCapabilities NormaliseCapabilities(Records.ChannelCapabilities declaredCapabilities)
    {
        if (declaredCapabilities.MaxMessageLength is not { } max)
            return declaredCapabilities;

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
        if (supervisor is null)
        {
            actorLogger.Warning("RestoreActiveAgent received but supervisor is not yet created for {0}; dropping request", conversationId);
            return;
        }

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

        if (supervisor is not null)
        {
            Context.Stop(supervisor);

            supervisor = null;

            actorLogger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
        }

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

        if (supervisor == null)
        {
            supervisor = await Context.System.GetOrCreateActorAsync<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);

            actorLogger.Warning("Supervisor was missing; created new supervisor: {0}", supervisor.Path);
        }

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor.Path);

        // Use Tell instead of Ask to support streaming
        supervisor.Tell(msg);
    }

    /// <summary>
    /// Handles streaming chunks from the supervisor and forwards them to the client via the active channel.
    /// Enables real-time progressive response rendering in the UI.
    /// </summary>
    /// <param name="chunk">Streaming chunk containing partial response text</param>
    /// <remarks>
    /// Chunks are suppressed entirely when the active channel does not advertise
    /// <see cref="Records.ChannelCapabilities.SupportsStreaming"/>. The final complete message
    /// still reaches the client via <see cref="HandleConversationResponseAsync"/>, so no content
    /// is lost — only the progressive rendering effect is skipped.
    /// </remarks>
    private async Task HandleStreamChunkAsync(Records.AgentStreamChunk chunk)
    {
        // Skip streaming entirely on channels that don't support it.
        // The final response is delivered as a single structured message by HandleConversationResponseAsync.
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out Records.ChannelMetadata? registeredMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any stream chunk.");

        if (!registeredMetadata.Capabilities.SupportsStreaming)
            return;

        // Forward chunk to client via the active channel for progressive rendering
        try
        {
            await channelService.SendStreamChunkAsync(conversationId, chunk.Text);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send stream chunk to client");
            // Don't propagate error - continue streaming
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

        // Send response to client via the active channel
        try
        {
            await channelService.SendMessageAsync(new Records.ChannelMessage
            {
                ConversationId = conversationId,
                Text = response.Response,
                MessageType = "assistant",
                QuickReplies = response.QuickReplies,
                AgentName = response.AgentName ?? "Morgana",
                AgentCompleted = response.AgentCompleted,
                Timestamp = response.OriginalTimestamp ?? DateTime.UtcNow,
                RichCard = response.RichCard
            });

            actorLogger.Info(
                $"Response sent successfully to client via channel " +
                $"(#quickReplies: {response.QuickReplies?.Count ?? 0}," +
                $"hasRichCard: {response.RichCard != null})");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send channel message to client");

            // Attempt to send error notification to client
            try
            {
                await channelService.SendMessageAsync(new Records.ChannelMessage
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
    /// Actor lifecycle hook: called when actor starts.
    /// </summary>
    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");
        base.PreStart();
    }

    /// <summary>
    /// Actor lifecycle hook: called when actor stops.
    /// </summary>
    protected override void PostStop()
    {
        // Defensive cleanup: in case the actor stops without an explicit TerminateConversation
        // (supervision failure, system shutdown, ...), make sure the registry doesn't leak the entry.
        channelMetadataStore.UnregisterChannelMetadata(conversationId);

        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");
        base.PostStop();
    }
}