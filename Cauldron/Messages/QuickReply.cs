namespace Cauldron.Messages;

/// <summary>
/// Represents a quick reply button for user interaction in chat messages.
/// Quick replies provide suggested actions or responses that users can click instead of typing.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>Quick replies guide users to available actions and reduce typing friction.
/// They are typically displayed below presentation messages to showcase conversation capabilities.</para>
/// <para><strong>Single-Use Pattern:</strong></para>
/// <para>Once a quick reply is selected, all quick replies in that message become disabled
/// (tracked via ChatMessage.SelectedQuickReplyId). This prevents confusion and duplicate submissions.</para>
/// </remarks>
public class QuickReply
{
    /// <summary>
    /// Unique identifier for this quick reply.
    /// Typically matches the intent name (e.g., "billing", "contract").
    /// </summary>
    /// <remarks>
    /// Used to track which button was clicked via ChatMessage.SelectedQuickReplyId.
    /// </remarks>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display text shown on the button.
    /// Typically includes an emoji and descriptive text (e.g., "📄 View Invoices").
    /// </summary>
    /// <remarks>
    /// <para><strong>Best Practices:</strong></para>
    /// <list type="bullet">
    /// <item>Start with an emoji for visual appeal and quick recognition</item>
    /// <item>Use action-oriented language (e.g., "View", "Check", "Manage")</item>
    /// <item>Keep text concise (2-4 words)</item>
    /// <item>Ensure text fits on mobile screens</item>
    /// </list>
    /// </remarks>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Message text sent to the backend when this quick reply is clicked.
    /// This becomes the user's message in the conversation flow.
    /// </summary>
    /// <remarks>
    /// <para><strong>Value Guidelines:</strong></para>
    /// <list type="bullet">
    /// <item>Should be a natural, complete user request</item>
    /// <item>Should trigger the intended agent/intent via classification</item>
    /// <item>Can be longer and more descriptive than Label</item>
    /// <item>Example: Label="📄 View Invoices" → Value="Show me my latest invoices"</item>
    /// </list>
    /// <para><strong>Processing:</strong></para>
    /// <para>When clicked, this value is sent as a regular user message:
    /// POST /api/conversation/{id}/message with body: { "text": "{Value}" }</para>
    /// </remarks>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Special flag indicating that this QuickReply is the conversation termination hint.
    /// It will be displayed in primary color to differentiate from the other quick replies.
    /// </summary>
    public bool Termination { get; set; }
}