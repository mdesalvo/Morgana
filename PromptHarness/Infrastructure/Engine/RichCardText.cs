using System.Globalization;
using System.Text;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Renders a rich card as the plain text it puts on a user's screen, for <see cref="ExpectationChecker"/>'s <c>richCardContains</c> and for <see cref="LLMJudge"/> alike.
/// One renderer keeps the structural layer and the judge from disagreeing on what a card said; an agent's Formatting often puts the
/// answering figure on the card, so a judge shown only its title would convict a response that did answer.
/// </summary>
internal static class RichCardText
{
    /// <summary>Every piece of text the card renders, one component per line, sections recursed into.</summary>
    public static string Flatten(RichCard card)
    {
        // The heading first, as the card shows it above everything else.
        StringBuilder text = new StringBuilder();
        text.AppendLine(card.Title);

        // The subtitle is optional and shown under the heading only when the agent wrote one.
        if (!string.IsNullOrWhiteSpace(card.Subtitle))
            text.AppendLine(card.Subtitle);

        // Every component's text beneath, in the order the card draws them.
        AppendComponents(card.Components, text);

        // The whole card's text, without the line break that follows its last component.
        return text.ToString().TrimEnd();
    }

    /// <summary>Recursive half of <see cref="Flatten"/>, one branch per known component type.</summary>
    private static void AppendComponents(IEnumerable<CardComponent> components, StringBuilder text)
    {
        foreach (CardComponent component in components)
        {
            // Each component adds the text it shows: a key and its value, an item per list line, a section's heading before its own
            // components. A divider or an image shows no text, so it adds no line.
            switch (component)
            {
                case TextBlockComponent textBlock:
                    text.AppendLine(textBlock.Content);
                    break;
                case KeyValueComponent keyValue:
                    text.AppendLine(CultureInfo.InvariantCulture, $"{keyValue.Key}: {keyValue.Value}");
                    break;
                case ListComponent list:
                    foreach (string item in list.Items)
                        text.AppendLine(CultureInfo.InvariantCulture, $"- {item}");
                    break;
                case SectionComponent section:
                    text.AppendLine(section.Title);
                    if (!string.IsNullOrWhiteSpace(section.Subtitle))
                        text.AppendLine(section.Subtitle);
                    AppendComponents(section.Components, text);
                    break;
                case GridComponent grid:
                    foreach (GridItem item in grid.Items)
                        text.AppendLine(CultureInfo.InvariantCulture, $"{item.Key}: {item.Value}");
                    break;
                case BadgeComponent badge:
                    text.AppendLine(badge.Text);
                    break;
            }
        }
    }
}