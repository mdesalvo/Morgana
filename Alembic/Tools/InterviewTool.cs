using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.Contracts;

namespace Alembic.Tools;

/// <summary>
/// The interview the tools of every pass act on, with the tools all passes share and the ones a
/// pass takes up by overriding.
/// </summary>
/// <remarks>
/// <para>
/// Every method here returns a sentence <b>to the model</b>, not to the client. That return channel
/// is the point of having tools at all rather than a structured reply: a section that comes back
/// the wrong shape is reported to Alembic in the same turn and it corrects itself before the
/// client ever sees anything. A single malformed structured reply, by contrast, costs the client a
/// turn of their own interview.
/// </para>
/// <para>
/// The write tools also carry the <b>section labels</b>. Both composed layers use the same four,
/// which is exactly why the framework fences them, so a domain layer arriving unlabelled leaves
/// half the prompt without the markers the other half has. A label says which section this is, not
/// what it means: it is structure and structure is not left to a model remembering a rule.
/// </para>
/// <para>
/// Which of these a pass holds is decided by its phase class, not by prose. The functional pass has no
/// tool for an agent's instructions or formatting, so it cannot write them — the constraint is the
/// absence of a tool rather than a sentence asking for restraint.
/// </para>
/// <para>
/// A tool that serves a different purpose in different passes is virtual here and each pass that
/// holds it overrides it with its own description. The phase class declares its tools in the order
/// the model reads them: what stands, the writing, the checks, the screen from top to bottom and the
/// close. The four tools that are the same in every pass are declared here once and follow them.
/// </para>
/// </remarks>
public abstract class InterviewTool
{
    /// <summary>The interview these tools write into — see the constructor.</summary>
    protected readonly InterviewState interviewState;

    /// <summary>
    /// Whether this pass has already been handed Morgana's own layer of the composed prompt.
    /// </summary>
    /// <remarks>
    /// One instance of these tools serves one pass, so this is exactly "has this pass seen it": a
    /// step that reads the composed prompt twice pays for her half once.
    /// </remarks>
    private bool framework;

    /// <summary>The domain being built or evolved — see the constructor.</summary>
    protected readonly IDraftStateService draftStateService;

    /// <summary>The deterministic checks — see the constructor.</summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>Composes the prompt the authored agent will really read — see the constructor.</summary>
    private readonly IRecapService recapService;

    /// <summary>
    /// Binds the toolset to one interview.
    /// </summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
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

    /// <summary>
    /// Returns the toolkit as it currently stands.
    /// </summary>
    public virtual string GetToolkit()
    {
        if (interviewState.Agent.Tools.Count == 0)
            return "This agent declares no tools yet. That is a legal end state — an agent whose tools "
                   + "all arrive from an MCP server declares none here — but it must be a conclusion you reached by asking.";

        IEnumerable<string> rendered = interviewState.Agent.Tools.Select(t =>
            $"- {t.Name}: {t.Description ?? "(no description)"}"
            + (t.Parameters.Count == 0
                ? "\n    (takes nothing)"
                : string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "authored by you"}"
                    + (p.Required ? "" : ", optional")
                    + (p.Shared ? ", shared" : "")
                    + $"]: {p.Description ?? "(no description)"}"))));

        return "The toolkit as it stands:\n" + string.Join("\n", rendered);
    }

    /// <summary>
    /// Returns what earlier passes settled about this agent.
    /// </summary>
    /// <remarks>
    /// Each pass is a fresh agent with a fresh session, so nothing of the previous conversation
    /// carries over — deliberately, because a toolkit pass that still has the whole functional
    /// interview in its context spends it re-litigating decisions already taken. What must carry
    /// over is the configuration and the configuration is exactly what this returns.
    /// </remarks>
    public virtual string GetAgentSoFar()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing settled yet: this agent has no intent.";

        List<string> sections =
        [
            $"Intent '{interviewState.Intent.Name}': {interviewState.Intent.Description}",
            $"Opening sentence a user would send: {interviewState.Intent.DefaultValue}",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, interviewState.Agent.Target ?? "(no target)"),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, interviewState.Agent.Personality ?? "(no personality)")
        ];

        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Instructions))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, interviewState.Agent.Instructions));

        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Formatting))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, interviewState.Agent.Formatting));

        return "Settled in the earlier passes and not yours to reopen:\n\n"
               + string.Join("\n\n", sections);
    }

    /// <summary>
    /// How much one agent or the business itself may hold on record before it has to be tidied.
    /// </summary>
    /// <remarks>
    /// Not a storage limit: every step opens holding what is known about the agent in hand, so a
    /// record that grows without end is a step reading forty sentences to ask one question. Reaching
    /// it is a sign two facts have become one fact said twice, which is the pass's own to settle.
    /// </remarks>
    private const int MemoryCeiling = 14;

    /// <summary>
    /// Where what is found out is kept: with the agent in hand or with the business.
    /// </summary>
    /// <remarks>
    /// The map stands on the whole domain, holding an agent nobody will write to — so what is
    /// learned there is about the business or it is lost with that empty agent.
    /// </remarks>
    private List<KnownFact> Memory => interviewState.OnAnEntry
        ? interviewState.Agent.Known
        : (draftStateService.Current ?? new DomainDraft()).Learned;

    /// <summary>
    /// Writes down one thing the client has just said about how their work actually goes.
    /// </summary>
    /// <remarks>
    /// Every pass is a fresh session that reads what is written and nothing else, so what the client
    /// says about their trade is spent the moment the turn ends unless it is written down here. That
    /// is how a step three passes later comes to ask a shopkeeper what a system ought to be able to
    /// check: it never knew there was a shop. A fact about the agent in hand is kept with that agent
    /// and travels with it; a fact about the business itself is kept for the whole domain. Neither
    /// ever enters the domain — this is what the questions are made of, not what the agents say.
    /// </remarks>
    /// <param name="subject">What it is about, in one or two of the client's own words.</param>
    /// <param name="fact">The fact itself, one sentence, in their vocabulary.</param>
    /// <param name="corrects">
    /// What this puts right, where the client has just contradicted something on record: any part of
    /// the wrong sentence is enough to find it. A reading taken off their upload is exactly the kind
    /// of thing that gets corrected here and it must go, rather than sit under the truth.
    /// </param>
    [Description("Writes down one thing the client has just told you about how their work actually goes — not what you decided about it. Every step after this one opens holding what is on record for the agent in hand and for the business, so call it the moment an answer says something a later step would otherwise have to ask again: what they sell, who writes in and about what, what they open to answer it, what goes wrong, what they never do. One fact per call, in their own words. Never what you wrote into a section — that is already in the configuration and a second copy of it here is a second thing to keep true. Where what they just said contradicts something on record, say so in 'corrects' and the wrong one goes: a record nobody can correct is worse than no record, because every step after you will hold it and none of them can doubt it.")]
    public string NoteDomainFact(
        [Description("What this is about, in one or two of THEIR words — 'prices', 'deposits', 'custom cakes'. Facts about the same thing take the same subject: it is what another agent's step reads to decide whether to fetch this at all.")] string subject,
        [Description("One short sentence about their work, in their vocabulary, never about the agent: 'the catalogue changes with the season', 'custom cakes are quoted at an appointment, never over the counter'. Standing on one entry of the map it is kept with that agent; drawing the map or closing the domain it is kept for the business as a whole.")] string fact,
        [Description("Any part of the sentence already on record that this puts right, where they have just contradicted it — a reading taken off their upload most of all. Leave it out when nothing is being corrected.")] string? corrects = null)
    {
        string written = fact.Trim();
        string about = subject.Trim();

        if (written.Length == 0 || about.Length == 0)
            return "Nothing written down: a fact needs both a subject and a sentence.";

        List<KnownFact> kept = Memory;
        int dropped = corrects is { Length: > 0 } wrong ? Forget(kept, wrong) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        dropped += kept.RemoveAll(known =>
            known.Inferred && string.Equals(known.Subject, about, StringComparison.OrdinalIgnoreCase));

        if (kept.Any(known => string.Equals(known.Fact, written, StringComparison.OrdinalIgnoreCase)))
            return "That was already written down; nothing added.";

        if (kept.Count >= MemoryCeiling)
            return $"Nothing written down: {kept.Count} facts already stand here, which is as many as "
                   + "are worth carrying into a question. Two of them have become the same fact in "
                   + "different words — drop one with DropDomainFact or say what this one corrects.";

        kept.Add(new KnownFact(about, written, Inferred: false));

        return $"Written down under '{about}'"
               + (dropped > 0 ? $". {dropped} thing(s) that said otherwise are gone" : string.Empty)
               + $". {kept.Count} thing(s) now stand on record here.";
    }

    /// <summary>
    /// Takes something off the record that turned out not to be true of their work.
    /// </summary>
    /// <remarks>
    /// The one thing a memory owes whoever it is about. Alembic reads an uploaded configuration for
    /// what it says about the business and it can read it wrong; a client says something that was
    /// true last year. Left standing, either is worse than never having known: every step after this
    /// one opens holding it and none of them has any way to doubt it.
    /// </remarks>
    /// <param name="fact">Any part of the sentence to remove, enough to tell it from the others.</param>
    [Description("Takes something off the record that is not true of their work. Use it the moment the client says otherwise. Use it on your own readings of their upload without hesitation too — nobody confirmed those. Left standing, a wrong fact is held by every step after this one and none of them has any way to doubt it.")]
    public string DropDomainFact(
        [Description("Any part of the sentence to remove or its subject, enough to tell it from the others on record.")] string fact)
    {
        string wrong = fact.Trim();

        if (wrong.Length == 0)
            return "Nothing dropped: the payload was empty.";

        int dropped = Forget(Memory, wrong);

        return dropped == 0
            ? "Nothing here says that; nothing dropped."
            : $"{dropped} thing(s) gone from the record. No step after this one will hold them.";
    }

    /// <summary>
    /// Hands back what is on record about one of the other agents of this domain.
    /// </summary>
    /// <remarks>
    /// A step opens holding what is known about its own agent and the business, with only the subjects
    /// the other agents keep — a domain of nine agents read whole would be forty sentences carried into
    /// every question, most of them about counters this step will never touch. This is how the rest
    /// is reached, when a subject listed there turns out to bear on the question in hand: whether the
    /// counter next door already takes deposits decides whether this one should.
    /// </remarks>
    /// <param name="intent">The agent's own intent name, as the opening message lists it.</param>
    [Description("Hands back what is on record about one of the OTHER agents of this domain. You open holding what is known about the agent in hand and about the business. Of every other agent you hold only the subjects it keeps — a domain read whole would be forty sentences carried into every question, nearly all about counters this step will never touch. Call this when a subject listed against another agent bears on the question you are about to ask: whether the counter next door already takes deposits decides whether this one should.")]
    public string RecallAgent(
        [Description("The agent's own intent name, exactly as the opening message lists it.")] string intent)
    {
        string named = intent.Trim();
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An agent of this domain is an entry of the map, written or still ahead — the same universe
        // the opening message lists its neighbours from. Resolved against the written agents alone,
        // this denied the existence of every agent the interview had not reached yet, which is most
        // of them on the first agent and all of them the client had just dictated.
        IntentDraft? entry = draft.Intents.Concat(interviewState.Map).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, named, StringComparison.OrdinalIgnoreCase));

        AgentDraft? agent = draft.Agents.FirstOrDefault(agent =>
            string.Equals(agent.ID, named, StringComparison.OrdinalIgnoreCase));

        if (entry is null && agent is null)
            return $"There is no agent called '{named}' in this domain. The opening message lists them by name.";

        // What the map says about an agent nobody has opened yet is the whole of what is known about
        // it. It is worth more than a refusal: the routing sentence the client dictated is the
        // only account of that counter anybody has.
        if (agent is null || agent.Known.Count == 0)
            return string.IsNullOrWhiteSpace(entry?.Description)
                ? $"Nothing is on record about how they work at '{named}'."
                : $"Nothing is on record yet about how they work at '{named}'. The map describes it as: {entry.Description}";

        return $"What is known about '{named}':\n"
               + string.Join("\n", agent.Known.Select(known =>
                   $"- {known.Subject}: {known.Fact}" + (known.Inferred ? " (read off their configuration, not said)" : string.Empty)));
    }

    /// <summary>
    /// Removes whatever on record carries the wrong sentence, by subject or by wording.
    /// </summary>
    private static int Forget(List<KnownFact> kept, string wrong) =>
        kept.RemoveAll(known =>
            known.Fact.Contains(wrong, StringComparison.OrdinalIgnoreCase)
            || wrong.Contains(known.Fact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(known.Subject, wrong, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says what the step about to be asked adds to the agent in hand, over its question.
    /// </summary>
    /// <remarks>
    /// A step that lands saying nothing but its question leaves the client to work out what this
    /// screen is for from the question alone, which is the one thing it cannot tell them. Held apart
    /// from the question rather than written above it in the same breath, because the screen draws
    /// the two in two different voices: what they are told and what they are asked are read at
    /// different moments and one block at one size is read as neither. It belongs to the turn a step
    /// lands on and to no confirmation or follow-up after it.
    /// </remarks>
    [Description("States, over the question you are about to ask, what this step adds to the agent in hand. Call it in the turn a step lands on, before the question, every time without exception: a step that lands with nothing but a question leaves the client working out what this screen is for from the question alone. Call it in no other turn — a confirmation or a follow-up is not a landing and what stands there would say the interview had moved on when it has not. It is not room for anything else: never an answer to what they just said, never an apology, never a correction of your own last turn. Those belong in the question.")]
    public string SetStepPlacing(
        [Description("One short sentence in the second person, naming what THEY will be able to do once this step is settled, in the things their work is actually made of — the orders, the counter, the people who write in. Name neither the agent nor the step: both already stand lit on the screen. Where the sentence needs a subject it is the part of their work this agent stands for, in their own words. Two things it is never. One is a line about what software becomes able to do: 'this is where the catalogue stops being just words and becomes something you can really consult' is a brochure and tells a baker nothing. The other is a restatement of why this stage exists, which is written above for you and not for them. The test is simple: if the same sentence could open this step for a garage, a vet and a bank alike, it places nothing. Nor is it ever at the expense of the steps behind it: what those settled is already their configuration, so a step announcing that things now finally become real tells them the screens they have just filled in were a game.")] string placing)
    {
        string written = placing.Trim();

        if (written.Length == 0)
            return "Nothing placed: the payload was empty.";

        interviewState.PendingPlacing = written;

        return "That will stand above the question, quieter than it and apart from it. It is the only "
               + "thing on the screen telling them what this step is for, so the question itself "
               + "need not say it again.";
    }

    /// <summary>
    /// Shows the client, word for word, the prose just written for their agent.
    /// </summary>
    /// <remarks>
    /// The turn that asks whether a section is right is the one turn where the client is approving
    /// exact words, so those words stand apart from the sentence introducing them. Run into one
    /// paragraph the two become a single stretch of prose in which nothing marks where Alembic stops
    /// speaking and the agent's own text begins. An approval given to that approves nothing in
    /// particular, which is the whole of what this interview is for.
    /// </remarks>
    /// <param name="written">The section's prose exactly as it now stands, with nothing added around it.</param>
    public virtual string ShowWhatIsWritten(string written)
    {
        string prose = written.Trim();

        if (prose.Length == 0)
            return "Nothing shown: the payload was empty.";

        interviewState.PendingQuoted = prose;

        return "That will stand on its own above your question, exactly as you wrote it. Your own "
               + "sentence should introduce it and never contain it: they are approving these words, "
               + "so what they read has to be only these words.";
    }

    /// <summary>
    /// Puts a worked example in the answer box under the question about to be asked.
    /// </summary>
    /// <remarks>
    /// Called before every question that asks for something new, cut to the size of that question:
    /// what it teaches is register, length and level of detail, which is the whole of what a
    /// first-time client is missing and none of what a label naming the material would convey. An
    /// empty box under an open question teaches none of it and comes back as a word where a
    /// sentence was wanted — which is how a domain arrives thin and only shows its holes at the
    /// emit. Written by the pass rather than fixed in the UI, so it is an answer somebody in the
    /// client's own trade might have given rather than a stranger's business quoted at them.
    /// </remarks>
    public virtual string SetExample(string example)
    {
        // The box holds it as the client's own text, so a pair of quotation marks around it is text
        // they would be sending. One at a single end is what a half-escaped payload leaves behind.
        string written = example.Trim().Trim('"').Trim();

        if (written.Length == 0)
            return "No example put in the box: the payload was empty.";

        interviewState.PendingExample = written;

        return "The example will stand in the answer box under your question, greyed and goes the "
               + "moment they answer. It is the only thing on the screen telling them how long an "
               + "answer is worth writing.";
    }

    /// <summary>
    /// Attaches to the question about to be asked the one button that answers it without adding anything.
    /// </summary>
    /// <remarks>
    /// An acknowledgement the client may take or leave: where the model is already right — everything
    /// stated back stands, nothing is missing, there is nothing the agent should be kept off — the
    /// turn settles with a press instead of a typed sentence and a refining exchange nobody learns
    /// anything from. Two plain strings rather than an array of buttons, because that answer is the
    /// only one Alembic knows the whole of; everything the client actually contributes is a sentence
    /// only they can write and they write it in the box, which never closes. A schema admitting a
    /// list would have the model compose a second button before anything could drop it, which costs
    /// the tokens whether or not it is ever drawn.
    /// The id is Alembic's: nothing downstream tells two buttons apart when there is only one and
    /// asking for it would be a third string with no reader.
    /// </remarks>
    public virtual string SetChoice(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            return "No button attached: it needs both a label to read and the answer it sends.";

        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        return "The button will be drawn under your question. "
               + "The text box stays open, so the answer may still come in the client's own words.";
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    /// <remarks>
    /// Two sources and both are load-bearing. What the domain already holds arrived from earlier
    /// sittings or an uploaded configuration; what the map still holds is what this interview has
    /// promised to write next. A description only has to be told apart from both.
    /// </remarks>
    public virtual string GetExistingIntents()
    {
        IEnumerable<string> written = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} (already in the domain)");

        IEnumerable<string> planned = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} (on the map, not written yet)");

        List<string> all = [.. written, .. planned];

        return all.Count == 0
            ? "Nothing else claims a route: this is the only intent and nothing can collide with it."
            : "The descriptions the classifier weighs this one against:\n" + string.Join("\n", all);
    }

    /// <summary>
    /// True when some OTHER agent of the draft declares it may consult this one — the half of the
    /// consultation relation an agent cannot see from its own <c>Consults</c> list.
    /// </summary>
    private bool IsConsultedByOthers(AgentDraft agent)
        => (draftStateService.Current?.Agents ?? []).Any(other =>
               other.ID != agent.ID &&
               other.Code.Consults.Any(colleague =>
                   colleague.Instance is null
                   && string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Returns the prompt this agent's model will really read.
    /// </summary>
    public virtual async Task<string> GetComposedPrompt()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Agent.Target))
            return "Nothing to compose yet: the agent has no target.";

        AgentRecap recap = await recapService.ComposeAsync(
            interviewState.Agent,
            IsConsultedByOthers(interviewState.Agent));

        // Morgana's half is the same eleven thousand words for every agent of every domain and it is
        // already in this pass's own context once it has been read once. Sending it again on the
        // second reading — which the passes that hunt a sentence repeating hers are told to take —
        // doubles the request for nothing: what changed between the two is the agent's own prose and
        // only that. Whole the first time, because the comparison needs her words in front of it.
        int domain = framework ? recap.SystemPrompt.LastIndexOf(Constants.SectionLabels.Target, StringComparison.Ordinal) : -1;

        framework = true;

        return domain < 0
            ? "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt
            : "Morgana's own layer above this one has not changed since you read it. This is the part "
              + "that is yours, as her composer lays it out:\n\n" + recap.SystemPrompt[domain..];
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// </summary>
    /// <remarks>
    /// Checked against a probe domain — what the client already has, plus the agent under
    /// construction — because half of these rules are relational: an intent nothing routes to and a
    /// name colliding with a framework prompt are both invisible when an agent is examined alone.
    /// Findings about the client's other agents are filtered out: they are real, but they are not
    /// this pass's business and Alembic cannot fix them from here.
    /// </remarks>
    public virtual string GetFindings()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing to check yet: the intent has no name.";

        DomainDraft existing = draftStateService.Current ?? new DomainDraft();

        DomainDraft probe = new DomainDraft
        {
            Intents = [.. existing.Intents, interviewState.Intent],
            Agents = [.. existing.Agents, interviewState.Agent]
        };

        string mine = interviewState.Intent.Name!;

        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{mine}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{mine}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        return findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}"));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>
    /// Believed only as far as the state machine can confirm it. Which fields are set is a fact
    /// and facts are not a model's to assert.
    /// </remarks>
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

        IReadOnlyList<string> missing = interviewState.Missing();

        if (missing.Count > 0)
            return $"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again.");

        interviewState.ReadyForReview = true;

        return $"This pass is settled. Say it is done and what comes next: {NextStep}";
    }

    /// <summary>
    /// Says what is wrong with a name that has to survive into C# or nothing if it is fine.
    /// </summary>
    /// <remarks>
    /// Shape only and deliberately not the compiler: the point is to catch what a domain
    /// conversation actually produces — a space, a hyphen, a leading digit — while it is still free
    /// to change. The casing check is separate because it is a convention rather than a rule and it
    /// is worth stating: the generated method and the declaration have to read like the framework's
    /// own.
    /// <para>
    /// Internal rather than private: <see cref="CoherenceApplyTool"/> writes into agents that are
    /// no longer the one interview state under construction, but the identifier it writes still has
    /// to survive into the same C#, by the same rule.
    /// </para>
    /// </remarks>
    internal static string IdentifierComplaint(string name, string what, bool pascalCase)
    {
        bool shapeOk = name.Length > 0
                       && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        if (!shapeOk)
            return $"But '{name}' cannot be a C# identifier and the {what} becomes C# verbatim: "
                   + "it must start with a letter and carry only letters, digits and underscores. Call again with one that can.";

        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        return casingOk
            ? string.Empty
            : $"But the {what} should be {(pascalCase ? "PascalCase" : "camelCase")}, the way the framework's own are. Call again to fix it.";
    }

    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>
    /// Recorded either way. The size is a shape, not a gate: a Target of five sentences is still
    /// better than no Target and Alembic is told so it can tighten rather than blocked so it must.
    /// </remarks>
    protected static string Shaped(string section, string? value, int minimum, int maximum)
    {
        int sentences = CountSentences(value);

        if (sentences >= minimum && sentences <= maximum)
            return $"{section} recorded.";

        return $"{section} recorded, but it runs to {sentences} "
               + (sentences == 1 ? "sentence" : "sentences")
               + $" where this section's shape is {minimum} to {maximum}. "
               + (sentences < minimum
                   ? "It is saying less than the section is for. Fill it out and call again."
                   : "Tighten it and call again.");
    }

    /// <summary>
    /// Counts sentences crudely — terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string trimmed = value.Trim();
        int count = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] is not ('.' or '!' or '?'))
                continue;

            if (i == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}
