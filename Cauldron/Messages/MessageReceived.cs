namespace Cauldron.Messages;

/// <summary>
/// Record representing a message received from the backend via SignalR.
/// Sent by ConversationHub.ReceiveMessage to all clients in the conversation group.
/// </summary>
/// <param name="ConversationId">Unique identifier of the conversation this message belongs to</param>
/// <param name="Text">Message text content from the agent</param>
/// <param name="Timestamp">When the message was generated</param>
/// <param name="MessageType">Optional message type classification ("assistant", "presentation", "error")</param>
/// <param name="QuickReplies">Optional list of quick reply buttons for user interaction</param>
/// <param name="ErrorReason">Optional error details if MessageType is "error"</param>
/// <param name="AgentName">
/// Name of the agent that generated this message.
/// Examples: "Morgana", "Billing Agent (📄 Billing)", "Contract Agent (📄 Contract)"
/// </param>
/// <param name="AgentCompleted">
/// Indicates whether the agent has completed its task.
/// When true, the conversation returns to idle state (supervisor clears active agent).
/// </param>
/// <remarks>
/// <para><strong>SignalR Flow:</strong></para>
/// <code>
/// 1. User sends message via POST /api/conversation/{id}/message
/// 2. Backend processes message through actor pipeline
/// 3. Agent generates response
/// 4. ISignalRBridgeService formats response as MessageReceived
/// 5. ConversationHub.Clients.Group(conversationId).SendAsync("ReceiveMessage", messageReceived)
/// 6. MorganaSignalRService receives message
/// 7. OnMessageReceived event fires with message parameters
/// 8. Index.razor HandleMessageReceived updates UI
/// </code>
/// <para><strong>Message Types:</strong></para>
/// <list type="bullet">
/// <item><term>null or "assistant"</term><description>Regular agent response</description></item>
/// <item><term>"presentation"</term><description>Welcome/completion message with quick replies</description></item>
/// <item><term>"error"</term><description>Error message with ErrorReason details</description></item>
/// </list>
/// <para><strong>Agent Lifecycle:</strong></para>
/// <list type="bullet">
/// <item><term>AgentCompleted = false</term><description>Agent is still processing (multi-turn conversation)</description></item>
/// <item><term>AgentCompleted = true</term><description>Agent finished, return to idle (header shows "Morgana")</description></item>
/// </list>
/// <para><strong>Quick Replies:</strong></para>
/// <para>Typically present only in presentation messages. When user clicks a quick reply,
/// the button is disabled and the reply value is sent as a new user message.</para>
/// </remarks>
public record MessageReceived(
    string ConversationId,
    string Text,
    DateTime Timestamp,
    string? MessageType = null,
    List<QuickReply>? QuickReplies = null,
    string? ErrorReason = null,
    string? AgentName = null,
    bool AgentCompleted = false
);