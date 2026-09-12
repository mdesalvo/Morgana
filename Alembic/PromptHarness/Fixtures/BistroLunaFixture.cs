using Distiller2.Model;
using PromptHarness.Infrastructure;

namespace PromptHarness.Fixtures;

/// <summary>
/// A two-desk restaurant domain — table reservations and private events in the back room — played
/// by a scripted "domain expert" who never says "quick reply", "rich card", "consultation", or any
/// other of Alembic's own vocabulary.
/// </summary>
/// <remarks>
/// Chosen deliberately, not as a plausible-sounding placeholder. Two things are planted in it and
/// each is a defect this suite exists to catch rather than a detail of a restaurant.
/// <para>
/// The <c>AgentFormatting</c> answer asks, in the same breath, for both the legitimate use of a
/// closed-choice button (a clear yes/no before a booking commits) and the anti-pattern the doctrine
/// forbids (a button per open time slot — identification from a list that can grow, which must stay
/// a question in prose). A domain that only ever asks for the right thing would never catch a pass
/// that cannot tell the two apart, which is exactly what a first run of this fixture caught in the
/// live product before this harness existed.
/// </para>
/// <para>
/// The front desk's <c>Instructions</c> answer sends the customer away — ring the events office
/// about the back room — while the events desk's own tools can answer that very question. That is
/// the one sentence the closing step exists for: an edge and the prose it contradicts have to land
/// together, or the agent is handed a colleague as a function and told in the same prompt not to
/// touch the subject. It is also the shipped <c>Examples</c> domain's own historical defect, put
/// where a run can meet it.
/// </para>
/// </remarks>
public static class BistroLunaFixture
{
    /// <summary>Words the front desk's own intent is recognisable by, whatever the mapper names it.</summary>
    public static IReadOnlyList<string> FrontDesk { get; } = ["reserv", "book", "table", "availab"];

    /// <summary>Words the events desk's own intent is recognisable by, on the same terms.</summary>
    public static IReadOnlyList<string> EventsDesk { get; } = ["event", "private", "room", "party", "function"];

    /// <summary>The client's half of the DomainMapper pass, in order.</summary>
    public static IReadOnlyList<string> MappingScript { get; } =
    [
        "Two things really. Most people calling or messaging us want to check if we have a free table " +
        "and then book it for a certain day, time and party size. Separately, we have a back room we let " +
        "out for private do's — birthdays, small wedding lunches — and those enquiries are a different " +
        "job: whether the room is taken that day, what the set menus are, and someone takes the details " +
        "down for our events manager to ring back. Everything about the à la carte menu or outside " +
        "catering stays with us on the phone.",
        "No, that is everything for now."
    ];

    /// <summary>The client's half of the whole interview: the map, both desks and the closing step.</summary>
    public static DomainScript FullScript() => new(
        new Queue<string>(MappingScript),
        [
            new DeskScript(FrontDesk, new Dictionary<InterviewStep, Queue<string>>
            {
                [InterviewStep.AgentTarget] = new Queue<string>(
                [
                    "It checks table availability at Bistro Luna and books a reservation once the customer picks " +
                    "a day, time and party size. It should never take payment, change the seating plan, or promise " +
                    "a table type we do not guarantee, like a window seat. The back room is not its business."
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
                [InterviewStep.AgentTerritory] = new Queue<string>(
                [
                    "Whether we can seat somebody, and when. How the room is filling up on a given day, what is " +
                    "still open and what has gone — the table diary is ours and nobody else here can say."
                ]),
                [InterviewStep.AgentInstructions] = new Queue<string>(
                [
                    // The planted hand-off — see the remarks on this class.
                    "Always show the available slots first, then get the customer to confirm the day, time and " +
                    "party size before actually booking. A reservation should never be placed without an explicit " +
                    "yes from the customer, since it holds a table someone else could have had. If nothing is free " +
                    "at their preferred time, offer the two closest alternatives instead of just saying no. And if " +
                    "somebody asks about the back room or a private do, it should tell them that is our events " +
                    "office and give them that number to ring instead."
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
            }),
            new DeskScript(EventsDesk, new Dictionary<InterviewStep, Queue<string>>
            {
                [InterviewStep.AgentTarget] = new Queue<string>(
                [
                    "This one is for the back room. It says whether the room is already taken on a date, tells " +
                    "people which of our three set menus we do for a private do and takes down an enquiry for our " +
                    "events manager to ring back. It never settles the booking itself and it never quotes a final " +
                    "price — that is the manager's, once she has spoken to them."
                ]),
                [InterviewStep.AgentPersonality] = new Queue<string>(
                [
                    "Same house, a shade more formal. Somebody planning a wedding lunch wants to feel it is being " +
                    "taken seriously, not chatted at."
                ]),
                [InterviewStep.AgentToolkit] = new Queue<string>(
                [
                    "It needs to look at the room's diary for a date and say whether it is free or taken. And it " +
                    "needs to write the enquiry down: the date, how many people, what the occasion is and a phone " +
                    "number to ring back on. Same as the other one, we already know who it is if they are in the " +
                    "loyalty program, otherwise they tell us.",
                    "That is all it reaches, yes."
                ]),
                [InterviewStep.AgentTerritory] = new Queue<string>(
                [
                    "Whether the back room is spoken for on a date, and what a private do can be given here — the " +
                    "three set menus are ours to know. What it ends up costing is the manager's, not ours."
                ]),
                [InterviewStep.AgentInstructions] = new Queue<string>(
                [
                    "It should always look at the diary before saying anything about a date, never promise a price " +
                    "or a change to a set menu, and always read the enquiry back before it sends it over. It should " +
                    "say plainly that the manager rings back within the day rather than leaving people waiting on an " +
                    "answer it cannot give."
                ]),
                [InterviewStep.AgentFormatting] = new Queue<string>(
                [
                    "When the room is free on the date, say so with the date and lay the three set menus out plainly " +
                    "so they can be compared. For the enquiry, read back what was taken down as one block before it " +
                    "goes to the manager. Never show anybody else's booking that is in the diary, just whether the " +
                    "room is taken.",
                    "No, that covers it."
                ])
            })
        ],
        new Queue<string>(
        [
            "Yes, that is how it works in practice. When someone at the front asks about the back room, whoever is " +
            "on the desk looks the room up there and then instead of passing the call over — it is the same phone. " +
            "The other way round never happens: whoever handles the events side does not go and take table bookings."
        ]));
}
