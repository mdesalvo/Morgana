using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.Contracts;

namespace Alembic.Tools;

/// <summary>
/// The interview the tools of every pass act on, with the tools all passes share and the ones a pass takes up by overriding.
/// Every method returns a sentence to the model, never to the client, so a section that comes back the wrong shape is corrected in the same turn.
/// Which tools a pass holds is decided by its phase class: a pass without a tool for a section cannot write that section.
/// A tool serving a different purpose in different passes is virtual here and each pass holding it overrides it with its own description.
/// </summary>
public abstract class InterviewTool
{
    /// <summary>The interview these tools write into.</summary>
    protected readonly InterviewState interviewState;

    /// <summary>
    /// Whether this pass has already been handed Morgana's own layer of the composed prompt.
    /// One instance of these tools serves one pass, so a step that reads the composed prompt twice pays for her half once.
    /// </summary>
    private bool framework;

    /// <summary>The domain being built or evolved.</summary>
    protected readonly IDraftStateService draftStateService;

    /// <summary>The checks decidable by reading the Draft, run on the agent in hand.</summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>Composes the prompt the authored agent will really read.</summary>
    private readonly IRecapService recapService;

    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The checks decidable by reading the Draft.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    protected InterviewTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
    {
        this.interviewState = interviewState;
        this.draftStateService = draftStateService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
    }

    /// <summary>What comes after this pass, said to the model once the pass is settled.</summary>
    protected abstract string NextStep { get; }

    /// <summary>Returns the toolkit as it currently stands.</summary>
    public virtual string GetToolkit()
    {
        // An empty toolkit is legal, so the model is told it may stay so only as a conclusion it reached by asking.
        if (interviewState.Agent.Tools.Count == 0)
            return "This agent declares no tools yet. That is a legal end state — an agent whose tools "
                   + "all arrive from an MCP server declares none here — but it must be a conclusion you reached by asking.";

        // One line per tool and one per parameter, each parameter with the scope, optionality and sharing the emit will declare.
        IEnumerable<string> toolLines = interviewState.Agent.Tools.Select(t =>
            $"- {t.Name}: {t.Description ?? "(no description)"}"
            + (t.Parameters.Count == 0
                ? "\n    (takes nothing)"
                : string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "authored by you"}"
                    + (p.Required ? "" : ", optional")
                    + (p.Shared ? ", shared" : "")
                    + $"]: {p.Description ?? "(no description)"}"))));

        // The toolkit read back whole, which is how a pass checks what it wrote before going on.
        return "The toolkit as it stands:\n" + string.Join("\n", toolLines);
    }

    /// <summary>
    /// Returns what earlier passes settled about this agent.
    /// Each pass is a fresh session that would spend a carried-over interview re-litigating decisions, so what crosses is the configuration alone.
    /// </summary>
    public virtual string GetAgentSoFar()
    {
        // Before the map names the agent there is nothing it could have settled.
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing settled yet: this agent has no intent.";

        // The intent and the two sections every agent has once the map and the first passes ran, labelled as the composer labels them.
        List<string> sections =
        [
            $"Intent '{interviewState.Intent.Name}': {interviewState.Intent.Description}",
            $"Opening sentence a user would send: {interviewState.Intent.DefaultValue}",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, interviewState.Agent.Target ?? "(no target)"),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, interviewState.Agent.Personality ?? "(no personality)")
        ];

        // The later sections appear only once a pass has written them, so an absent one reads as not reached yet.
        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Instructions))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, interviewState.Agent.Instructions));

        // Formatting is the last section a pass writes.
        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Formatting))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, interviewState.Agent.Formatting));

        // Stated as settled fact, so the pass reads it as ground to stand on rather than as a draft to argue with.
        return "Settled in the earlier passes and not yours to reopen:\n\n"
               + string.Join("\n\n", sections);
    }

    /// <summary>
    /// How much one agent or the business itself may hold on record before it has to be tidied.
    /// Every step opens holding what is known, so a record without end is a step reading forty sentences to ask one question.
    /// </summary>
    private const int MemoryCeiling = 14;

    /// <summary>
    /// Where what is found out is kept: with the agent in hand or with the business.
    /// The map stands on the whole domain, holding an agent nobody will write to, so what is learned there goes to the business.
    /// </summary>
    private List<KnownFact> Memory => interviewState.OnAnEntry
        ? interviewState.Agent.Known
        : (draftStateService.Current ?? new DomainDraft()).Learned;

    /// <summary>
    /// Writes down one thing the client has just said about how their work actually goes.
    /// Every pass is a fresh session, so what the client says about their trade is lost when the turn ends unless it is written here;
    /// it never enters the domain, since it is what the questions are made of, not what the agents say.
    /// </summary>
    /// <param name="subject">What it is about, in one or two of the client's own words.</param>
    /// <param name="fact">The fact itself, one sentence, in their vocabulary.</param>
    /// <param name="corrects">Any part of a sentence on record that the client has just contradicted, which then goes rather than sitting under the truth.</param>
    [Description("Writes down one thing the client has just told you about how their work actually goes — not what you decided about it. Every step after this one opens holding what is on record for the agent in hand and for the business, so call it the moment an answer says something a later step would otherwise have to ask again: what they sell, who writes in and about what, what they open to answer it, what goes wrong, what they never do. One fact per call, in their own words. Never what you wrote into a section — that is already in the configuration and a second copy of it here is a second thing to keep true. Where what they just said contradicts something on record, say so in 'corrects' and the wrong one goes: a record nobody can correct is worse than no record, because every step after you will hold it and none of them can doubt it.")]
    public string NoteDomainFact(
        [Description("What this is about, in one or two of THEIR words — 'prices', 'deposits', 'custom cakes'. Facts about the same thing take the same subject: it is what another agent's step reads to decide whether to fetch this at all.")] string subject,
        [Description("One short sentence about their work, in their vocabulary, never about the agent: 'the catalogue changes with the season', 'custom cakes are quoted at an appointment, never over the counter'. Standing on one entry of the map it is kept with that agent; drawing the map or closing the domain it is kept for the business as a whole.")] string fact,
        [Description("Any part of the sentence already on record that this puts right, where they have just contradicted it — a reading taken off their upload most of all. Leave it out when nothing is being corrected.")] string? corrects = null)
    {
        // The sentence and the subject as the model wrote them, without the spaces a payload carries at its ends.
        string factText = fact.Trim();
        string subjectText = subject.Trim();

        // A fact without either part could never be found again by a later step, so nothing is filed.
        if (factText.Length == 0 || subjectText.Length == 0)
            return "Nothing written down: a fact needs both a subject and a sentence.";

        // The record this pass writes to, then whatever the client has just contradicted taken off it.
        List<KnownFact> memoryFacts = Memory;
        int droppedCount = corrects is { Length: > 0 } wrongText ? Forget(memoryFacts, wrongText) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        droppedCount += memoryFacts.RemoveAll(known =>
            known.Inferred && string.Equals(known.Subject, subjectText, StringComparison.OrdinalIgnoreCase));

        // The same sentence twice would be one fact said twice, which every later step would carry.
        if (memoryFacts.Any(known => string.Equals(known.Fact, factText, StringComparison.OrdinalIgnoreCase)))
            return "That was already written down; nothing added.";

        // A full record means two facts have become one, which is the pass's own to tidy before it adds.
        if (memoryFacts.Count >= MemoryCeiling)
            return $"Nothing written down: {memoryFacts.Count} facts already stand here, which is as many as "
                   + "are worth carrying into a question. Two of them have become the same fact in "
                   + "different words — drop one with DropDomainFact or say what this one corrects.";

        // Filed as said by the client, which no later reading of their upload can override.
        memoryFacts.Add(new KnownFact(subjectText, factText, Inferred: false));

        // The model learns what went and how full the record now is, which is what decides its next note.
        return $"Written down under '{subjectText}'"
               + (droppedCount > 0 ? $". {droppedCount} thing(s) that said otherwise are gone" : string.Empty)
               + $". {memoryFacts.Count} thing(s) now stand on record here.";
    }

    /// <summary>
    /// Takes something off the record that turned out not to be true of their work.
    /// A wrong fact left standing is worse than none, since every step after this one opens holding it and none can doubt it.
    /// </summary>
    /// <param name="fact">Any part of the sentence to remove, enough to tell it from the others.</param>
    [Description("Takes something off the record that is not true of their work. Use it the moment the client says otherwise. Use it on your own readings of their upload without hesitation too — nobody confirmed those. Left standing, a wrong fact is held by every step after this one and none of them has any way to doubt it.")]
    public string DropDomainFact(
        [Description("Any part of the sentence to remove or its subject, enough to tell it from the others on record.")] string fact)
    {
        // The words to look for, without the spaces a payload carries at its ends.
        string wrongText = fact.Trim();

        // An empty search would match every fact on record.
        if (wrongText.Length == 0)
            return "Nothing dropped: the payload was empty.";

        // Whatever carries those words, by subject or by wording, leaves the record this pass writes to.
        int droppedCount = Forget(Memory, wrongText);

        // A drop that found nothing is said plainly, so the model does not believe a wrong fact gone.
        return droppedCount == 0
            ? "Nothing here says that; nothing dropped."
            : $"{droppedCount} thing(s) gone from the record. No step after this one will hold them.";
    }

    /// <summary>
    /// Hands back what is on record about one of the other agents of this domain.
    /// A step opens holding only the subjects the other agents keep, so this is how one of them is read when it bears on the question in hand.
    /// </summary>
    /// <param name="intent">The agent's own intent name, as the opening message lists it.</param>
    [Description("Hands back what is on record about one of the OTHER agents of this domain. You open holding what is known about the agent in hand and about the business. Of every other agent you hold only the subjects it keeps — a domain read whole would be forty sentences carried into every question, nearly all about counters this step will never touch. Call this when a subject listed against another agent bears on the question you are about to ask: whether the counter next door already takes deposits decides whether this one should.")]
    public string RecallAgent(
        [Description("The agent's own intent name, exactly as the opening message lists it.")] string intent)
    {
        // The intent asked for and the domain it is looked up in, empty before anything was imported or written.
        string intentName = intent.Trim();
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An agent of this domain is an entry of the map, written or still ahead — the same universe
        // the opening message lists its neighbours from. Resolved against the written agents alone,
        // this denied the existence of every agent the interview had not reached yet, which is most
        // of them on the first agent and all of them the client had just dictated.
        IntentDraft? entry = draft.Intents.Concat(interviewState.Map).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, intentName, StringComparison.OrdinalIgnoreCase));

        // The written agent, which carries the record; absent for an entry the interview has not reached.
        AgentDraft? agent = draft.Agents.FirstOrDefault(agent =>
            string.Equals(agent.ID, intentName, StringComparison.OrdinalIgnoreCase));

        // A name neither on the map nor written is a guess the model should correct against the opening message.
        if (entry is null && agent is null)
            return $"There is no agent called '{intentName}' in this domain. The opening message lists them by name.";

        // What the map says about an agent nobody has opened yet is the whole of what is known about
        // it. It is worth more than a refusal: the routing sentence the client dictated is the
        // only account of that counter anybody has.
        if (agent is null || agent.Known.Count == 0)
            return string.IsNullOrWhiteSpace(entry?.Description)
                ? $"Nothing is on record about how they work at '{intentName}'."
                : $"Nothing is on record yet about how they work at '{intentName}'. The map describes it as: {entry.Description}";

        // The record whole, each reading of an upload marked as such so the model does not take it for the client's word.
        return $"What is known about '{intentName}':\n"
               + string.Join("\n", agent.Known.Select(known =>
                   $"- {known.Subject}: {known.Fact}" + (known.Inferred ? " (read off their configuration, not said)" : string.Empty)));
    }

    /// <summary>Removes whatever on record carries the wrong sentence, by subject or by wording, either way round.</summary>
    private static int Forget(List<KnownFact> kept, string wrong) =>
        kept.RemoveAll(known =>
            known.Fact.Contains(wrong, StringComparison.OrdinalIgnoreCase)
            || wrong.Contains(known.Fact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(known.Subject, wrong, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says what the step about to be asked adds to the agent in hand, over its question.
    /// It is held apart from the question because the screen draws the two in different voices, read at different moments.
    /// </summary>
    [Description("States, over the question you are about to ask, what this step adds to the agent in hand. Call it in the turn a step lands on, before the question, every time without exception: a step that lands with nothing but a question leaves the client working out what this screen is for from the question alone. Call it in no other turn — a confirmation or a follow-up is not a landing and what stands there would say the interview had moved on when it has not. It is not room for anything else: never an answer to what they just said, never an apology, never a correction of your own last turn. Those belong in the question.")]
    public string SetStepPlacing(
        [Description("One short sentence in the second person, naming what THEY will be able to do once this step is settled, in the things their work is actually made of — the orders, the counter, the people who write in. Name neither the agent nor the step: both already stand lit on the screen. Where the sentence needs a subject it is the part of their work this agent stands for, in their own words. Two things it is never. One is a line about what software becomes able to do: 'this is where the catalogue stops being just words and becomes something you can really consult' is a brochure and tells a baker nothing. The other is a restatement of why this stage exists, which is written above for you and not for them. The test is simple: if the same sentence could open this step for a garage, a vet and a bank alike, it places nothing. Nor is it ever at the expense of the steps behind it: what those settled is already their configuration, so a step announcing that things now finally become real tells them the screens they have just filled in were a game.")] string placing)
    {
        // The sentence as the model wrote it, without the spaces a payload carries at its ends.
        string placingText = placing.Trim();

        // An empty line would draw an empty band above the question.
        if (placingText.Length == 0)
            return "Nothing placed: the payload was empty.";

        // Held until the turn ends, when the screen draws it above the question.
        interviewState.PendingPlacing = placingText;

        // The model learns the question need not repeat what this line already says.
        return "That will stand above the question, quieter than it and apart from it. It is the only "
               + "thing on the screen telling them what this step is for, so the question itself "
               + "need not say it again.";
    }

    /// <summary>
    /// Shows the client, word for word, the prose just written for their agent.
    /// The client is approving exact words, so they stand apart from the sentence introducing them: run together, an approval would approve nothing in particular.
    /// </summary>
    /// <param name="written">The section's prose exactly as it now stands, with nothing added around it.</param>
    public virtual string ShowWhatIsWritten(string written)
    {
        // The section's words as the model wrote them, without the spaces a payload carries at its ends.
        string prose = written.Trim();

        // An empty quote would ask the client to approve nothing.
        if (prose.Length == 0)
            return "Nothing shown: the payload was empty.";

        // Held until the turn ends, when the screen draws it apart from the question.
        interviewState.PendingQuoted = prose;

        // The model learns its own sentence must introduce the words and never contain them.
        return "That will stand on its own above your question, exactly as you wrote it. Your own "
               + "sentence should introduce it and never contain it: they are approving these words, "
               + "so what they read has to be only these words.";
    }

    /// <summary>
    /// Puts a worked example in the answer box under the question about to be asked.
    /// It teaches register, length and detail, which a first-time client lacks; written by the pass, it comes from the client's own trade.
    /// </summary>
    public virtual string SetExample(string example)
    {
        // The box holds it as the client's own text, so a pair of quotation marks around it is text
        // they would be sending. One at a single end is what a half-escaped payload leaves behind.
        string exampleText = example.Trim().Trim('"').Trim();

        // An empty example would leave the box as empty as no example.
        if (exampleText.Length == 0)
            return "No example put in the box: the payload was empty.";

        // Held until the turn ends, when the screen greys it into the answer box.
        interviewState.PendingExample = exampleText;

        // The model learns the example goes the moment the client answers.
        return "The example will stand in the answer box under your question, greyed and goes the "
               + "moment they answer. It is the only thing on the screen telling them how long an "
               + "answer is worth writing.";
    }

    /// <summary>
    /// Attaches to the question about to be asked the one button that answers it without adding anything.
    /// Two plain strings rather than a list, because the answer that adds nothing is the only one Alembic knows whole:
    /// a schema admitting a list would have the model compose a second button, paid for whether or not it is drawn.
    /// </summary>
    public virtual string SetChoice(string label, string value)
    {
        // A button needs words to read and an answer to send: without either, pressing it says nothing.
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            return "No button attached: it needs both a label to read and the answer it sends.";

        // The id is Alembic's: with one button nothing downstream tells two apart.
        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        // The model learns the text box stays open beside the button.
        return "The button will be drawn under your question. "
               + "The text box stays open, so the answer may still come in the client's own words.";
    }

    /// <summary>
    /// Returns the intents a description has to be told apart from: those the domain holds and those the map has still to write.
    /// </summary>
    public virtual string GetExistingIntents()
    {
        // What earlier sittings or an upload already wrote, the agent in hand left out.
        IEnumerable<string> writtenIntents = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} (already in the domain)");

        // What this interview has promised to write next.
        IEnumerable<string> plannedIntents = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} (on the map, not written yet)");

        List<string> rivalIntents = [.. writtenIntents, .. plannedIntents];

        // The classifier weighs every description against all the others, so the model reads them all.
        return rivalIntents.Count == 0
            ? "Nothing else claims a route: this is the only intent and nothing can collide with it."
            : "The descriptions the classifier weighs this one against:\n" + string.Join("\n", rivalIntents);
    }

    /// <summary>True when another agent of the draft declares it may consult this one, the half of the relation an agent's own list cannot show.</summary>
    private bool IsConsultedByOthers(AgentDraft agent)
        => (draftStateService.Current?.Agents ?? []).Any(other =>
               other.ID != agent.ID &&
               other.Code.Consults.Any(colleague =>
                   colleague.Instance is null
                   && string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Returns the prompt this agent's model will really read.</summary>
    public virtual async Task<string> GetComposedPrompt()
    {
        // Without a Target the domain layer would compose around nothing.
        if (string.IsNullOrWhiteSpace(interviewState.Agent.Target))
            return "Nothing to compose yet: the agent has no target.";

        // Composed as Morgana composes it, with the consultation policy where some colleague may consult this agent.
        AgentRecap recap = await recapService.ComposeAsync(
            interviewState.Agent,
            IsConsultedByOthers(interviewState.Agent));

        // Morgana's half is the same eleven thousand words for every agent of every domain and it is
        // already in this pass's own context once it has been read once. Sending it again on the
        // second reading — which the passes that hunt a sentence repeating hers are told to take —
        // doubles the request for nothing: what changed between the two is the agent's own prose and
        // only that. Whole the first time, because the comparison needs her words in front of it.
        int domainStart = framework ? recap.SystemPrompt.LastIndexOf(Constants.SectionLabels.Target, StringComparison.Ordinal) : -1;

        // From here on this pass holds her layer.
        framework = true;

        // Whole the first time, the agent's own part after that.
        return domainStart < 0
            ? "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt
            : "Morgana's own layer above this one has not changed since you read it. This is the part "
              + "that is yours, as her composer lays it out:\n\n" + recap.SystemPrompt[domainStart..];
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// Half the rules are relational, so the agent is checked inside the whole domain, then only its own findings are kept.
    /// </summary>
    public virtual string GetFindings()
    {
        // The findings are filtered by the intent's name, so there is nothing to filter by without one.
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing to check yet: the intent has no name.";

        // What the client already has, empty on a domain born in this interview.
        DomainDraft existing = draftStateService.Current ?? new DomainDraft();

        // The domain as it would stand with the agent in hand added, which is what a relational rule needs to see.
        DomainDraft probe = new DomainDraft
        {
            Intents = [.. existing.Intents, interviewState.Intent],
            Agents = [.. existing.Agents, interviewState.Agent]
        };

        string intentName = interviewState.Intent.Name!;

        // The findings about this agent or the domain as a whole: those about the client's other agents are real but not this pass's to fix.
        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{intentName}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{intentName}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        // Each finding with its severity, where and the rule it rests on, so the model can fix it and say why.
        return findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}"));
    }

    /// <summary>
    /// Declares the pass settled, believed only as far as the state machine can confirm it.
    /// Which fields are set is a fact and facts are not a model's to assert.
    /// </summary>
    public virtual string SetPassCompleted()
    {
        // Correcting, every section is written already, so each pass could settle the moment it
        // opened and the client would be walked through an edit that asked them nothing at all —
        // which is how a client who came to change a voice reached the end with the voice they
        // came to change. A pass may still settle on their first word; the doctrine's own fast
        // path stands: what it may not do is settle before they have said one.
        if (interviewState.Revision is not null && interviewState.Exchanges == interviewState.PassOpenedAt)
            return "Not completed: this section is reopened and the client has not said a word about it yet. "
                   + "State what it says today, attach the choice that agrees with it and settle it on their answer.";

        // The fields this pass must have set, as the state machine counts them.
        IReadOnlyList<string> missing = interviewState.Missing();

        // A pass with a field still unset is not settled, whatever the model believes.
        if (missing.Count > 0)
            return $"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again.");

        // The state machine opens the next pass inside the same answer.
        interviewState.ReadyForReview = true;

        // The model announces the close and what follows, which only the phase class knows.
        return $"This pass is settled. Say it is done and what comes next: {NextStep}";
    }

    /// <summary>
    /// Says what is wrong with a name that has to survive into C#; empty when it is fine.
    /// It checks the shape a conversation actually produces (a space, a hyphen, a leading digit) while it is free to change, then the framework's casing;
    /// <see cref="CoherenceApplyTool"/> calls it too, for the identifiers it writes into other agents.
    /// </summary>
    internal static string IdentifierComplaint(string name, string what, bool pascalCase)
    {
        // A C# identifier starts with a letter or an underscore and carries only letters, digits and underscores.
        bool shapeOk = name.Length > 0
                       && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        // A name that cannot compile is refused with the rule, so the model can pick one that can.
        if (!shapeOk)
            return $"But '{name}' cannot be a C# identifier and the {what} becomes C# verbatim: "
                   + "it must start with a letter and carry only letters, digits and underscores. Call again with one that can.";

        // Methods and classes are PascalCase, parameters camelCase, as in the framework's own C#.
        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        // A casing slip is a convention, stated so the generated code reads like the framework's.
        return casingOk
            ? string.Empty
            : $"But the {what} should be {(pascalCase ? "PascalCase" : "camelCase")}, the way the framework's own are. Call again to fix it.";
    }

    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it, the section being recorded either way.
    /// The size is a shape and not a gate: a long Target is still better than none, so Alembic is told to tighten rather than blocked.
    /// </summary>
    protected static string Shaped(string section, string? value, int minimum, int maximum)
    {
        // The section's length in sentences, the unit its doctrine is stated in.
        int sentences = CountSentences(value);

        // Inside its shape, the section needs nothing more said.
        if (sentences >= minimum && sentences <= maximum)
            return $"{section} recorded.";

        // Outside it, the model is told by how much and which way to correct.
        return $"{section} recorded, but it runs to {sentences} "
               + (sentences == 1 ? "sentence" : "sentences")
               + $" where this section's shape is {minimum} to {maximum}. "
               + (sentences < minimum
                   ? "It is saying less than the section is for. Fill it out and call again."
                   : "Tighten it and call again.");
    }

    /// <summary>Counts sentences crudely: terminal punctuation followed by a space or the end.</summary>
    private static int CountSentences(string? value)
    {
        // An unwritten section has no sentences.
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string trimmed = value.Trim();
        int count = 0;

        // Every terminal mark that closes a sentence counts; one inside a number or an abbreviation does not.
        for (int characterIndex = 0; characterIndex < trimmed.Length; characterIndex++)
        {
            // Only a full stop, an exclamation or a question mark can end a sentence.
            if (trimmed[characterIndex] is not ('.' or '!' or '?'))
                continue;

            // It ends one only before a space or at the very end.
            if (characterIndex == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[characterIndex + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}