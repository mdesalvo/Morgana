using System.Text;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Renders a rich card as the plain text it puts on a user's screen.
/// </summary>
/// <remarks>
/// One renderer, two consumers — <see cref="ExpectationChecker"/>'s <c>richCardContains</c> and the
/// view <see cref="LLMJudge"/> is handed — so the structural layer and the judge can never disagree
/// about what a card actually said. A card is not decoration: an agent's Formatting routinely puts
/// the figure that answers the question on it, so a judge shown only the title is reading half the
/// screen and convicts a response that did answer.
/// </remarks>
internal static class RichCardText
{
    /// <summary>Every piece of text the card renders, one component per line, sections recursed into.</summary>
    public static string Flatten(RichCard card)
    {
        StringBuilder text = new StringBuilder();

        text.AppendLine(card.Title);
        if (!string.IsNullOrWhiteSpace(card.Subtitle))
            text.AppendLine(card.Subtitle);

        AppendComponents(card.Components, text);

        return text.ToString().TrimEnd();
    }

    /// <summary>Recursive half of <see cref="Flatten"/>, one branch per known component type.</summary>
    private static void AppendComponents(IEnumerable<CardComponent> components, StringBuilder text)
    {
        foreach (CardComponent component in components)
        {
            switch (component)
            {
                case TextBlockComponent textBlock:
                    text.AppendLine(textBlock.Content);
                    break;
                case KeyValueComponent keyValue:
                    text.AppendLine($"{keyValue.Key}: {keyValue.Value}");
                    break;
                case ListComponent list:
                    foreach (string item in list.Items)
                        text.AppendLine($"- {item}");
                    break;
                case SectionComponent section:
                    text.AppendLine(section.Title);
                    if (!string.IsNullOrWhiteSpace(section.Subtitle))
                        text.AppendLine(section.Subtitle);
                    AppendComponents(section.Components, text);
                    break;
                case GridComponent grid:
                    foreach (GridItem item in grid.Items)
                        text.AppendLine($"{item.Key}: {item.Value}");
                    break;
                case BadgeComponent badge:
                    text.AppendLine(badge.Text);
                    break;
            }
        }
    }
}