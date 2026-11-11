using System.Text.Json;
using Microsoft.Agents.AI;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>The record of a conversation: each participant's session, its channel, its shared context and the transcript built from them.</summary>
// The one place that knows how a row encodes its messages, Morgana's own row included.
public interface IConversationPersistenceService
{
    /// <summary>
    /// Saves an agent's session, its messages and context state, marking whether the agent handed the conversation back.
    /// A row that SaveParticipantMessagesAsync rewrote behind the agent keeps that rewrite: an implementation appends only the
    /// messages the agent added since, writes its context state as it stands and leaves the row dirty until the agent reads it.
    /// A fault of I/O, encryption or serialization throws, because a session that silently failed to save would be lost.
    /// </summary>
    /// <param name="agentIdentifier">The agent as its turns address it, <c>{agent_name}-{conversation_id}</c>.</param>
    /// <param name="agent">The running agent whose session is saved.</param>
    /// <param name="agentSession">The session to save.</param>
    /// <param name="isCompleted">True when the agent signalled that it hands the conversation back.</param>
    /// <param name="jsonSerializerOptions">The serializer options; null for the library's defaults.</param>
    Task SaveAgentConversationAsync(
        string agentIdentifier,
        AIAgent agent,
        AgentSession agentSession,
        bool isCompleted,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>The agent's saved session, restored onto <paramref name="agent"/>; null when the agent never saved one, so a new session is started.</summary>
    /// <param name="agentIdentifier">The agent as its turns address it, <c>{agent_name}-{conversation_id}</c>.</param>
    /// <param name="agent">The agent that receives the restored session.</param>
    /// <param name="jsonSerializerOptions">The serializer options; null for the library's defaults.</param>
    Task<AgentSession?> LoadAgentConversationAsync(
        string agentIdentifier,
        Abstractions.MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>The agent currently carrying the conversation, such as <c>billing</c>; null when none is.</summary>
    Task<string?> GetMostRecentActiveAgentAsync(string conversationId);

    /// <summary>
    /// Takes an agent off active duty, leaving its session and messages as they are, so the record names no agent the conversation was handed back from.
    /// A row already inactive or missing is left as it is. A failure is logged and absorbed, because the turn it follows has already been answered.
    /// </summary>
    /// <param name="conversationId">The conversation whose record is read or written.</param>
    /// <param name="agentName">The agent as its row names it, such as <c>billing</c>.</param>
    Task DeactivateAgentAsync(string conversationId, string agentName);

    /// <summary>
    /// The transcript of the conversation across every participant, in chronological order; empty while nobody has spoken.
    /// A row that cannot be read fails the whole call, so a channel never draws a transcript with silent gaps.
    /// </summary>
    /// <param name="conversationId">The conversation whose record is read or written.</param>
    /// <param name="jsonSerializerOptions">The serializer options; null for the library's defaults.</param>
    Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Appends what Morgana says in her own voice to her row, creating it on the first write.
    /// The welcome, a refusal, a disambiguation and the handing-back line are filed here, beside the agents' rows, so the transcript
    /// has no gaps; the user's phrase is filed here too whenever no agent was active. This reaches Morgana's row only, because an
    /// agent's row carries a live session that the agent restores itself from. Appends arrive concurrently, the user's phrase on
    /// one path and an answer on another, so an implementation reads, extends and writes back as one step. The row is never active:
    /// Morgana is not an agent a conversation can be resumed onto.
    /// </summary>
    /// <param name="conversationId">The conversation whose record is read or written.</param>
    /// <param name="messages">The messages in the order they were spoken; an empty list writes nothing.</param>
    Task AppendOrchestratorMessagesAsync(
        string conversationId,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages);

    /// <summary>The messages of an agent's row as they were written, for work done on the record outside a turn; empty when the agent has no row.</summary>
    /// <param name="conversationId">The conversation whose record is read or written.</param>
    /// <param name="agentName">The agent as its row names it, such as <c>billing</c>.</param>
    Task<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>> LoadParticipantMessagesAsync(string conversationId, string agentName);

    /// <summary>
    /// Writes <paramref name="messages"/> back as an agent's messages, leaving the rest of its row as it was.
    /// The session carries context state beside its history, none of which is the caller's to rewrite. A row that does not exist
    /// is refused rather than created, since an agent with no row has no session to correct. Messages that the agent appended past
    /// messagesReadCount are kept as found, while a row that grew shorter is one the caller no longer describes and nothing is
    /// written. A rewritten row is dirty for its agent until LoadAgentConversationAsync reads it again.
    /// </summary>
    /// <param name="conversationId">The conversation whose record is read or written.</param>
    /// <param name="agentName">The agent as its row names it.</param>
    /// <param name="messages">The agent's messages in order, as they are to stand on record.</param>
    /// <param name="messagesReadCount">How many messages the row held when the caller read it.</param>
    /// <returns>True when the row was rewritten; false when the agent left it in a state that the caller cannot speak for.</returns>
    Task<bool> SaveParticipantMessagesAsync(
        string conversationId,
        string agentName,
        IReadOnlyList<Microsoft.Extensions.AI.ChatMessage> messages,
        int messagesReadCount);

    /// <summary>Creates the conversation's database with the current schema when it does not exist yet; calling it again changes nothing.</summary>
    // A partner's exchange arrives with no handshake, so the hosted agent opens its record here before the turn spends anything.
    Task EnsureDatabaseInitializedAsync(string conversationId);

    /// <summary>Persists the channel that the conversation settled at its handshake, creating the conversation's database when needed.</summary>
    // The handshake is the first write of a conversation started by a channel, so the database may not exist yet.
    Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata);

    /// <summary>The channel persisted for the conversation; null when it has no database or no handshake, which callers refuse to serve.</summary>
    Task<ChannelMetadata?> LoadChannelMetadataAsync(string conversationId);

    /// <summary>Records a shared context variable in the conversation's registry for every agent to read, the first value written winning.</summary>
    // A later write of another value is ignored rather than overwriting the first one. It may run before the agent's first save,
    // so an implementation creates the database when needed.
    Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent);

    /// <summary>Every shared context variable written so far in the conversation; empty when there are none or no database exists.</summary>
    // Every agent reads it at the start of each turn and merges it through MorganaAIContextProvider.MergeSharedContext, which
    // keeps the values the agent's own session already holds.
    Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId);

    /// <summary>Tells whether the conversation exists on record, telling a genuine conversation from an identifier never materialised.</summary>
    // Asynchronous because a store other than SQLite may answer over the network.
    Task<bool> ConversationExistsAsync(string conversationId);

    /// <summary>Tells whether SaveParticipantMessagesAsync rewrote the agent's row since the agent last read it, leaving its copy in memory behind the record.</summary>
    /// <param name="agentIdentifier">The agent as its turns address it, <c>{agent_name}-{conversation_id}</c>.</param>
    Task<bool> IsDirtyAsync(string agentIdentifier);
}