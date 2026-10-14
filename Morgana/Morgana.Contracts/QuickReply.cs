using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// Interactive button displayed to the user for quick action selection.
/// Used in presentation messages and agent responses: the framework builds it from what a turn offers, the model never writes one.
/// </summary>
/// <param name="Id">Unique identifier for the quick reply (typically matches intent name or action)</param>
/// <param name="Label">Display text shown on the button with emoji (e.g., "📄 View Invoices")</param>
/// <param name="Value">Message text sent when user clicks the button (e.g., "Show my invoices")</param>
/// <param name="Termination">Reserved flag indicating that the button is used as "exit to Morgana"</param>
[Description("A button offered to the user for a quick choice.")]
public record QuickReply(
    [property: JsonPropertyName("id")]
    [property: Description("The identifier of the button, which tells the channels what pressing it does.")]
    string Id,

    [property: JsonPropertyName("label")]
    [property: Description("The text shown on the button; an emoji is welcome.")]
    string Label,

    [property: JsonPropertyName("value")]
    [property: Description("The message sent on the user's behalf when the button is pressed.")]
    string Value,

    [property: JsonPropertyName("termination")]
    [property: Description("True when pressing the button ends the conversation with the agent and returns to Morgana.")]
    bool? Termination=false)
{
    /// <summary>A button counts this many characters beyond its label when a message is measured against a channel's length budget.</summary>
    private const int ButtonPadding = 4;

    /// <summary>
    /// Estimated rendering footprint of this quick reply button in visual characters.
    /// Only the visible label contributes — Id/Value are wire-only and never surface to the user.
    /// </summary>
    public int EstimateCost() => Label.Length + ButtonPadding;
}