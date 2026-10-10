using System.Text.Json;
using Microsoft.Agents.AI;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// The record of a conversation: each agent's session, Morgana's own messages, the channel handshake and the
/// context variables that agents share. A conversation must be restorable from it alone after the process serving it is gone.
/// </summary>
public interface IConversationPersistenceService
{
    /// <summary>
    /// Saves the complete conversation state of the given agent to persistent storage.
    /// The session's message history and context variables are kept so that <see cref="LoadAgentConversationAsync"/> restores them.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation</param>
    /// <param name="agent">AIAgent instance corresponding to the running agent</param>
    /// <param name="agentSession">AgentSession instance containing the complete conversation state</param>
    /// <param name="isCompleted">Flag indicating if the agent is signalling completion of the conversation</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <remarks>
    /// <para><strong>Rewrites behind the agent:</strong></para>
    /// <para>A row rewritten by <see cref="SaveParticipantMessagesAsync"/> since the agent last saved or read it
    /// must keep that rewrite: implementations append only the messages the agent added since, write its
    /// context state as it stands and leave the row dirty until <see cref="LoadAgentConversationAsync"/> reads it.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>Implementations should throw meaningful exceptions for I/O errors, encryption failures and
    /// serialization errors to allow proper error handling by callers.</para>
    /// </remarks>
    Task SaveAgentConversationAsync(
        string agentIdentifier,
        AIAgent agent,
        AgentSession agentSession,
        bool isCompleted,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Loads a previously saved agent's conversation state from persistent storage.
    /// The restored session has its AI context providers reconnected, so the agent can resume on it as it stands.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation to load</param>
    /// <param name="agent">MorganaAgent instance that will receive the deserialized session</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Deserialized AgentSession if conversation exists, null if not found</returns>
    /// <remarks>
    /// <para>Returns null when the agentIdentifier has never been saved, indicating this is a new conversation.
    /// Callers should create a new AgentSession in this case via agent.GetNewSessionAsync().</para>
    /// </remarks>
    Task<AgentSession?> LoadAgentConversationAsync(
        string agentIdentifier,
        Abstractions.MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Gets the agent that was engaged last in a conversation, which is the one a returning client carries on with.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>Agent name (e.g., "billing") or null if conversation not found</returns>
    Task<string?> GetMostRecentActiveAgentAsync(string conversationId);

    /// <summary>
    /// Retrieves the complete conversation history across all agents for a given conversation.
    /// Merges the messages of Morgana and of every agent that took part into one chronological dialogue.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Array of MorganaChatMessage ordered by creation timestamp, or empty array if conversation not found</returns>
    /// <remarks>
    /// <para>Fails fast on any read error: no partial history is returned, so a channel always displays a complete, consistent conversation.</para>
    /// </remarks>
    Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Appends messages to the orchestrator's own side of the conversation, creating it on first
    /// write. What Morgana says in her own voice is filed here — the welcome, a refusal, a
    /// disambiguation, the line handing a conversation back — beside the agents' own rows, so that
    /// <see cref="GetConversationHistoryAsync"/> returns a dialogue with no gaps for a channel to
    /// guess at. The user's phrase is saved here too, whenever no agent was active when it arrived.
    /// </summary>
    /// <param name="conversationId">Conversation whose orchestrator side is being appended to.</param>
    /// <param name="messages">Messages to append, in the order they were spoken. An empty sequence writes nothing.</param>
    /// <remarks>
    /// <para>Deliberately not addressable by author: this reaches one row and only that row. An agent's
    /// row carries a live agent session that the agent resurrects itself from, so messages appended to
    /// it from outside would discard everything else that session holds. The orchestrator owns no
    /// session and no model, which is what makes her row safe to append to from anywhere.</para>
    /// <para>Concurrent appends are expected — the user's phrase arrives on one path while an answer
    /// leaves on another — so implementations must read, extend and write back as one step.</para>
    /// <para>The row is never left active: the orchestrator is not an agent a conversation can be
    /// resumed onto. What reports the active agent must keep naming real agents only.</para>
    /// </remarks>
    Task AppendOrchestratorMessagesAsync(
        string conversationId,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages);

    /// <summary>
    /// Reads the messages an agent's row holds, as they were written, for work done on the record rather
    /// than inside a turn. Empty when that agent has no row in this conversation.
    /// </summary>
    /// <param name="conversationId">Conversation the agent belongs to.</param>
    /// <param name="agentName">The agent as its row names it, such as "billing".</param>
    Task<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> LoadParticipantMessagesAsync(string conversationId, string agentName);

    /// <summary>
    /// Writes <paramref name="messages"/> back as that agent's messages, leaving the rest of its row exactly
    /// as it was: an agent's session carries context state beside its history, none of which is the caller's
    /// to rewrite. Implementations must refuse a row that does not exist rather than create one, since an
    /// agent with no row has no session to correct.
    /// </summary>
    /// <param name="conversationId">Conversation the agent belongs to.</param>
    /// <param name="agentName">The agent as its row names it.</param>
    /// <param name="messages">The agent's messages, in order, as they are to stand on record.</param>
    /// <param name="messagesReadCount">
    /// How many messages the caller had when it composed <paramref name="messages"/>. An agent speaking
    /// meanwhile appends to its own row, so whatever arrived past that point is kept as it is found; a row
    /// that instead grew shorter is one the caller no longer describes, so nothing is written.
    /// </param>
    /// <returns>True when the row was rewritten; false when the agent left it in a state this caller cannot speak for.</returns>
    /// <remarks>A row rewritten here turns dirty for its agent until <see cref="LoadAgentConversationAsync"/> reads it again.</remarks>
    Task<bool> SaveParticipantMessagesAsync(
        string conversationId,
        string agentName,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        int messagesReadCount);

    /// <summary>
    /// Ensures the conversation's record exists in its current schema. Idempotent: calling it again changes nothing.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <remarks>
    /// A user may write before any agent has run, so every writer that can be first calls this before it writes.
    /// </remarks>
    Task EnsureDatabaseInitializedAsync(string conversationId);

    /// <summary>
    /// Persists the channel metadata (channel name + capability budget) declared by the client
    /// at conversation start. A conversation has one channel on record: a second call replaces the first.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <param name="metadata">Metadata advertised by the originating channel.</param>
    /// <remarks>
    /// <para><strong>First-writer pattern:</strong></para>
    /// <para>This method may be the very first persistence call for a brand-new conversation
    /// (the channel handshake happens before any agent has executed). The implementation
    /// MUST therefore call <see cref="EnsureDatabaseInitializedAsync(string)"/> internally so
    /// that the record exists before the metadata is written.</para>
    /// </remarks>
    Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata);

    /// <summary>
    /// Loads the channel metadata previously persisted for a conversation. Returns
    /// <c>null</c> when the conversation has no record or no metadata on it:
    /// such a conversation has no channel on record and callers refuse to serve it.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <returns>The persisted <see cref="ChannelMetadata"/>, or null if absent.</returns>
    Task<ChannelMetadata?> LoadChannelMetadataAsync(string conversationId);

    /// <summary>
    /// Persists a shared context variable into the conversation's registry, where every agent of the conversation reads it.
    /// First-write-wins: implementations MUST ignore subsequent upserts with different values.
    /// May be invoked before agent's first save; implementations MUST call EnsureDatabaseInitializedAsync internally.
    /// </summary>
    Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent);

    /// <summary>
    /// Loads all shared context variables that have been written to the conversation's registry up to this point. Called by every agent at the start of
    /// each turn (after the agent's session is loaded/created) so that variables produced by
    /// any sibling agent — including ones that no longer exist as live actors — are available
    /// to the current agent's tools.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <returns>
    /// Dictionary of variable name → value. Empty dictionary when the conversation has no
    /// shared variables yet (or when the database does not yet exist).
    /// </returns>
    /// <remarks>
    /// The caller is expected to feed the returned dictionary into
    /// <see cref="MorganaAIContextProvider.MergeSharedContext"/>, which itself enforces
    /// first-write-wins on the agent-local side: variables already in the agent's own session
    /// are not overwritten by the registry.
    /// </remarks>
    Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId);

    /// <summary>
    /// Reports whether the conversation has a record. The restore path uses this to tell a genuine conversation
    /// from an identifier that never materialized.
    /// </summary>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns><c>true</c> if the conversation is present in the store, <c>false</c> otherwise.</returns>
    bool ConversationExists(string conversationId);

    /// <summary>
    /// Whether the agent's row was rewritten by <see cref="SaveParticipantMessagesAsync"/> since the agent
    /// last read it through <see cref="LoadAgentConversationAsync"/>: its copy in memory is then behind its record.
    /// </summary>
    /// <param name="agentIdentifier">The agent as its turns address it, <c>{agent_name}-{conversation_id}</c>.</param>
    Task<bool> IsDirtyAsync(string agentIdentifier);
}