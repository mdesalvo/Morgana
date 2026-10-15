using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Alembic.Models;

/// <summary>
/// One button of an agent's menu as the model writes it: what the button leads to and what it shows.
/// </summary>
[Description("One button of the menu.")]
public sealed record MenuEntryDraft(
    [property: JsonPropertyName("entry")]
    [property: Description("The exact name of the tool or the workflow that the button presses.")]
    string Entry,
 
    [property: JsonPropertyName("label")]
    [property: Description("What the button shows and the message that its press sends.")]
    string Label);