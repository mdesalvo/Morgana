using Distiller.Interfaces;
using Distiller.Model;

namespace PromptHarness.Fixtures;

/// <summary>
/// A single-agent restaurant table booking domain, played by a scripted "domain expert" who never
/// says "quick reply", "rich card", or any other of Alembic's own vocabulary.
/// </summary>
/// <remarks>
/// Chosen deliberately, not as a plausible-sounding placeholder: its <see cref="AgentFormatting"/>
/// answer asks, in the same breath, for both the legitimate use of a closed-choice button (a clear
/// yes/no before a booking commits) and the anti-pattern the doctrine forbids (a button per open
/// time slot — identification from a list that can grow, which must stay a question in prose). A
/// domain that only ever asks for the right thing would never catch a pass that cannot tell the two
/// apart, which is exactly what a first run of this fixture caught in the live product before this
/// harness existed.
/// </remarks>
public static class BistroLunaFixture
{
    /// <summary>The client's half of the DomainMapper pass, in order.</summary>
    public static IReadOnlyList<string> MappingScript { get; } =
    [
        "People calling or messaging us mostly want to check if we have a free table and then book " +
        "it for a certain day, time and party size. That is really the only thing I want handled for " +
        "now, everything about the menu or catering stays with us on the phone.",
        "No, that is everything for now."
    ];

    /// <summary>The client's half of the whole interview, queued per pass.</summary>
    public static Dictionary<InterviewStep, Queue<string>> FullScript() => new()
    {
        [InterviewStep.DomainMapper] = new Queue<string>(MappingScript),
        [InterviewStep.AgentTarget] = new Queue<string>(
        [
            "It checks table availability at Bistro Luna and books a reservation once the customer picks " +
            "a day, time and party size. It should never take payment, change the seating plan, or promise " +
            "a table type we do not guarantee, like a window seat."
        ]),
        [InterviewStep.AgentPersonality] = new Queue<string>(
        [
            "Warm and welcoming, like the maitre d' greeting you at the door: efficient but never cold. " +
            "Think a friendly host, not a call center."
        ]),
        [InterviewStep.AgentToolkit] = new Queue<string>(
        [
            "We already know who is calling if they are in our loyalty program, otherwise anyone can book " +
            "by giving their name and phone number. It needs to check what tables are free for a day, time " +
            "and party size. It needs to actually place the reservation once the customer picks a slot, " +
            "which needs the day, time, party size and the customer's name and phone number. It must " +
            "not commit anything without a final confirmation from the customer.",
            "Nothing about the customer sticks around between calls except their loyalty id, if they have one."
        ]),
        [InterviewStep.AgentInstructions] = new Queue<string>(
        [
            "Always show the available slots first, then get the customer to confirm the day, time and " +
            "party size before actually booking. A reservation should never be placed without an explicit " +
            "yes from the customer, since it holds a table someone else could have had. If nothing is free " +
            "at their preferred time, offer the two closest alternatives instead of just saying no."
        ]),
        [InterviewStep.AgentFormatting] = new Queue<string>(
        [
            // The adversarial turn — see the remarks on this class.
            "When I show the open slots, I would like the customer to just tap the time they want instead " +
            "of typing it back to me. And once a booking is confirmed, showing everything together, day, " +
            "time, party size, name, like a little confirmation ticket, would be nicer than a paragraph. " +
            "Before it actually books, I want the customer to clearly say yes or no, not just type whatever " +
            "— an actual clear choice, so nothing gets double-booked by a misunderstanding.",
            "No, that covers it."
        ])
    };
}
