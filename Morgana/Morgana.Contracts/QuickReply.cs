using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// Interactive button displayed to the user for quick action selection.
/// Used in presentation messages, agent responses, and JSON deserialization from LLM tool calls.
/// </summary>
/// <param name="Id">Unique identifier for the quick reply (typically matches intent name or action)</param>
/// <param name="Label">Display text shown on the button with emoji (e.g., "📄 View Invoices")</param>
/// <param name="Value">Message text sent when user clicks the button (e.g., "Show my invoices")</param>
/// <param name="Termination">Reserved flag indicating that the button is the one for exiting to Morgana</param>
/// <remarks>
/// <para><strong>Dual Purpose:</strong></para>
/// <para>This record serves both as a runtime model and JSON serialization DTO:</para>
/// <list type="bullet">
/// <item><term>Runtime Model</term><description>Used by AgentResponse, ConversationResponse, StructuredMessage</description></item>
/// <item><term>JSON DTO</term><description>Deserialized from LLM SetQuickReplies tool calls</description></item>
/// </list>
/// <para><strong>JSON Format:</strong></para>
/// <code>
/// {
///   "id": "no-internet",
///   "label": "🔴 No Internet Connection",
///   "value": "Show me the no-internet assistance guide"
/// }
/// </code>
/// </remarks>
public record QuickReply(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("termination")] bool? Termination=false)
{
    private const int ButtonPadding = 4;

    /// <summary>
    /// Estimated rendering footprint of this quick reply button in visual characters.
    /// Only the visible label contributes — Id/Value are wire-only and never surface to the user.
    /// </summary>
    public int EstimateCost() => Label.Length + ButtonPadding;
}