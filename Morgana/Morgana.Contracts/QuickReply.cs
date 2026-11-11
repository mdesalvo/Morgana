using System.Text.Json.Serialization;

namespace Morgana.Contracts;

/// <summary>
/// Interactive button displayed to the user for quick action selection.
/// The model generates quick replies through tool calls and this record is deserialised from them, so its shape is a stable contract.
/// </summary>
/// <param name="Id">Typically matches the intent name or action the button stands for.</param>
/// <param name="Label">The text shown on the button.</param>
/// <param name="Value">The message text sent on the user's behalf when the button is clicked.</param>
/// <param name="Termination">Reserved flag marking the button as an "exit to Morgana".</param>
public record QuickReply(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("termination")] bool? Termination=false)
{
    // The visual characters that the button frame adds around its label.
    private const int ButtonPadding = 4;

    /// <summary>
    /// Estimated rendering footprint of this button in visual characters.
    /// Only the visible label contributes because Id and Value are wire-only and never surface to the user.
    /// </summary>
    public int EstimateCost() => Label.Length + ButtonPadding;
}