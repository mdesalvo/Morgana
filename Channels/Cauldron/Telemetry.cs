using System.Diagnostics.Metrics;

namespace Cauldron;

/// <summary>Holds Cauldron's meter with the counters and the attribute glossary that its metrics share.</summary>
public static class Telemetry
{
    /// <summary>The single meter for all Cauldron metrics.</summary>
    public static readonly Meter CauldronMeter = new Meter("Cauldron");

    /// <summary>Counts the security events at Cauldron's doors, tagged by door and outcome and never by domain.</summary>
    // The domain is chosen by whoever wrote the URL, so a hostile one would multiply the series of the metric without
    // limit.
    public static readonly Counter<long> SecurityEvents = CauldronMeter.CreateCounter<long>("cauldron.security.events", description: "Security events at Cauldron's doors: images withheld because they come from a domain outside Cauldron:TrustedImageDomains");

    /// <summary>The attribute naming the door where a security event happened.</summary>
    public const string SecurityDoor = "security.door";

    /// <summary>The attribute naming what Cauldron did about a security event.</summary>
    public const string SecurityOutcome = "security.outcome";

    /// <summary>The values of <see cref="SecurityDoor"/>.</summary>
    public static class SecurityDoors
    {
        /// <summary>An image written in Markdown, in the text of a reply or in a field of a card.</summary>
        public const string MarkdownImage = "markdown_image";

        /// <summary>The <c>image</c> component of a card.</summary>
        public const string CardImage = "card_image";
    }

    /// <summary>The values of <see cref="SecurityOutcome"/>.</summary>
    public static class SecurityOutcomes
    {
        /// <summary>The image was not shown.</summary>
        public const string Withheld = "withheld";
    }
}