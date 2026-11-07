using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Tools;

/// <summary>The tools of the pass that maps the domain into its kinds of request.</summary>
public sealed class DomainMapperTool : InterviewTool
{
    /// <summary>
    /// The intent name the framework reserves for the classifier's fallback. No authored agent may
    /// take it.
    /// </summary>
    private const string ReservedFallbackIntent = Constants.Intents.Other;

    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public DomainMapperTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
        : base(interviewState, draftStateService, draftValidationService, recapService)
    {
    }

    /// <inheritdoc />
    protected override string NextStep => $"the first of the {interviewState.Map.Count} kinds of request you mapped, taken one at a time until every one has its agent.";

    /// <summary>
    /// Returns the domain map as it currently stands.
    /// </summary>
    /// <remarks>
    /// Read back whole for the same reason the toolkit is: two descriptions overlap or they do not;
    /// that is only visible side by side. It is the one defect no prose downstream repairs — the
    /// user meets it as the wrong agent answering.
    /// </remarks>
    [Description("Returns the map as it stands, in order. Read it before settling: whether two descriptions could claim the same message is only visible with both in front of you.")]
    public string GetDomainMap()
    {
        if (interviewState.Map.Count == 0)
            return "The map is empty: no kind of request has been named yet.";

        return "The domain map as it stands. The descriptions are weighed against each other by the classifier, "
               + "and the buttons are read side by side by a user:\n"
               + string.Join("\n", interviewState.Map.Select((i, n) =>
                   $"{n + 1}. {i.Name}: {i.Description ?? "(nothing said about what routes here)"}"
                   + $"\n    button: {i.Label ?? "(none)"} → \"{i.DefaultValue ?? "(nothing)"}\""));
    }

    /// <inheritdoc />
    [Description("Returns what this domain already routes, from earlier sittings or an uploaded configuration, with the descriptions the classifier weighs against. An overlap you never looked at is a collision no prose fixes afterwards.")]
    public override string GetExistingIntents() => base.GetExistingIntents();

    /// <summary>
    /// Puts one kind of request on the domain map or revises one already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map is the interview's spine: one entry becomes one intent and one agent and the three
    /// later passes run once down the list. Revising keeps the entry's place, because the order is
    /// the order the client thought of their own business in and the interview walks it in that
    /// order.
    /// </para>
    /// <para>
    /// All four fields of an intent and the two that arrive later arrive here too. The button and
    /// the sentence it sends are read by a user <em>beside every other intent's button</em>, exactly
    /// as the descriptions are weighed beside every other description — so they are written where
    /// the whole set is visible and not one at a time in an agent's own pass where nothing can be
    /// compared to anything.
    /// </para>
    /// </remarks>
    /// <param name="name">Bare lowercase word — becomes a C# attribute argument and a prompt ID.</param>
    /// <param name="description">What routes here, weighed by the classifier against every other intent's.</param>
    /// <param name="label">The quick-reply button text a user reads, if this call is also settling it.</param>
    /// <param name="defaultValue">The sentence pressing that button sends, as if the user had typed it.</param>
    [Description("Puts one process the client is handing to Morgana on the domain map or revises one already there. It writes the whole intent — what it is called, what routes to it and the button a user presses to start it — because all three are read against the other entries and not on their own. One entry becomes one intent and one agent. Call it as soon as an entry is clear and again to add the button or sharpen the description.")]
    public string DeclareIntent(
        [Description("A single bare lowercase word, no spaces or punctuation (e.g. 'billing', 'appointments'). It becomes a C# attribute argument and a prompt ID. 'other' is reserved for the classifier's fallback and the names of Morgana's own prompts (morgana, classifier, guard, presentation, channeladapter) are hers: both will be refused.")] string name,
        [Description("The kinds of request that belong here — 'requests to check an order's status, place a new order or cancel one' — never what an agent does about them. The classifier reads this and nothing else, weighed against every other entry's description.")] string description,
        [Description("A leading emoji then one to three words (e.g. '📄 Billing'). It is read on a button beside every other intent's button, so it names what is got, not what the system does. Omit it on a first pass and add it when the set is written together.")] string? label = null,
        [Description("The sentence that button sends. First person, phrased the way it would actually be typed (e.g. 'I need to book an appointment for my dog'). Omit it on a first pass and add it with the label.")] string? defaultValue = null)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "Nothing recorded: an intent must have a name — it becomes a C# attribute argument and a prompt ID.";

        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return $"Nothing recorded: '{ReservedFallbackIntent}' is reserved. It is the intent the classifier "
                   + "falls back to when it cannot place a message and no agent may claim it. Call this again with a name from the domain.";

        // An intent named after a framework prompt is refused by Morgana at startup, so it is refused here while it costs nothing.
        if (Constants.Prompts.All.Contains(cleanName, StringComparer.OrdinalIgnoreCase))
            return $"Nothing recorded: '{cleanName}' is the name of one of Morgana's own prompts and an agent's prompt is found "
                   + "by its intent's name, so Morgana would refuse to start. Call this again with a name from the domain.";

        if (draftStateService.Current?.Intents.Any(i =>
                string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) == true)
            return $"Nothing recorded: '{cleanName}' is already an intent of this domain, written before today. "
                   + "Two agents answering the same intent is a startup failure. Name what is different about this one.";

        IntentDraft? existing = interviewState.Map.FirstOrDefault(i =>
            string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        bool revision = existing is not null;
        IntentDraft intent = existing ?? new IntentDraft { Origin = Provenance.Authored };

        intent.Name = cleanName;
        intent.Description = description?.Trim();

        // Left alone when the call omits them, so declaring an entry early and giving it a button
        // later is one entry revised twice rather than a button silently thrown away.
        if (!string.IsNullOrWhiteSpace(label))
            intent.Label = label.Trim();

        if (!string.IsNullOrWhiteSpace(defaultValue))
            intent.DefaultValue = defaultValue.Trim();

        if (!revision)
            interviewState.Map.Add(intent);

        List<string> complaints = [];

        if (!cleanName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            complaints.Add("But that is not a bare lowercase word and it becomes a C# attribute argument and a prompt ID. Call this again with one that is.");

        if (string.IsNullOrWhiteSpace(description))
            complaints.Add("It says nothing about what routes here, which is the sentence the classifier weighs against every other intent.");

        if (string.IsNullOrWhiteSpace(intent.Label) || string.IsNullOrWhiteSpace(intent.DefaultValue))
            complaints.Add("It has no button yet: the map is not settled until every entry carries the words a user reads and the sentence pressing them sends.");

        return (revision ? $"'{cleanName}' revised on the map." : $"'{cleanName}' is on the map, in position {interviewState.Map.Count}.")
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>
    /// Takes a kind of request off the map.
    /// </summary>
    [Description("Takes an entry off the map, for when two turn out to be one or the client says it is not their business.")]
    public string DropIntent(
        [Description("The name of the entry to remove.")] string name)
    {
        int removed = interviewState.Map.RemoveAll(i =>
            string.Equals(i.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removed > 0
            ? $"'{name}' is off the map."
            : $"Nothing dropped: '{name}' is not on the map.";
    }

    /// <inheritdoc />
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins. An approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    public override string ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] string written)
        => base.ShowWhatIsWritten(written);

    /// <inheritdoc />
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over. On the opening turn of THIS pass call it only if GetExistingIntents came back with a domain: those intents say what business this is, so an example can be written in their trade. Against an empty domain you know nothing about them yet — do not call it and the house example stands instead. From the second question on you do know their trade, because they have just described it, so every open question of this pass carries an example written out of what they said.")]
    public override string SetExample(
        [Description("An answer somebody in THEIR trade might have given, cut to the size of the question — forty to sixty words on the opening turn, ten to twenty on a follow-up — drawn from what the business has been said to do, by the existing intents on the opening turn and by the client themselves after it: which parts of the work they are handing over and which they are keeping. It teaches the register, the length and the level of detail at once and must be close enough to be worth editing without being agreeable as it stands. Never the question again in other words: what it holds has to be missing from the question above it.")] string example)
        => base.SetExample(example);

    /// <inheritdoc />
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it BEFORE writing the question — the button renders below it. EVERY turn that states back what you have just written carries it, without exception: that turn's whole question is whether they agree. A client with nothing to object to should not have to type a sentence to say so. So does every question asking whether something exists — anything missing, anything the agent should be kept off, anything somebody else in their place takes instead — since 'no, nothing' is always an honest answer to it, however it is worded. Never a button carrying an answer about their work, where it would put your words in place of theirs. The text box stays open either way.")]
    public override string SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question AS YOU PUT IT — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] string value)
        => base.SetChoice(label, value);

    /// <inheritdoc />
    [Description("Declares the map settled. Checked against the state machine, which confirms every entry has a name and a description before agreeing.")]
    public override string SetPassCompleted() => base.SetPassCompleted();
}
