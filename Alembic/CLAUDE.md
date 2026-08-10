# Alembic — Morgana's Authoring Workbench

## What is Alembic

Alembic is a **Blazor Server** web application that gives a client the *initial morganization* turnkey:
an AI-conducted functional interview that distils a domain expert's answers into a complete Morgana
domain — intents, agent prose, tool contracts, C# assets and non-regression scenarios.

The name is the point. Every other unit in the repo names an instrument (Cauldron the vessel,
Grimoire the book, Rune the mark, PromptHarness the rig); an *alembic* is the apparatus that
distils, which is what this does to a rambling interview.

Alembic lives at `Alembic/` in the repo root, alongside `Morgana/`, `Channels/`, `Examples/` and
`PromptHarness/`, and has its own solution (`Alembic.slnx`).

## What Alembic is not

- **Not a channel.** It never calls a Morgana instance, holds no JWT, announces no `ChannelMetadata`,
  joins no conversation pipeline. It is the only unit in the repo that is not a client of a running
  Morgana. Its sole external dependency is an LLM.
- **Not a filesystem tool.** At runtime Alembic lives wherever the client deployed it — a cloud, an
  on-prem box next to Morgana, a laptop — exactly like Cauldron, and its position in this repo says
  nothing about that. So it makes **no assumption of seeing the client's filesystem**: configuration
  arrives as an **upload** and leaves as a **download**.

That second point is load-bearing and settles a question that would otherwise recur: Alembic cannot
know which C# already exists on the client's side, so it never tries to guess, patch or merge it. See
*Regeneration contract* below for what replaces that.

## Design decisions

### Performance tier, non-negotiable

Alembic runs on `Records.LLMTier.Performance`, and not out of caution. Its whole job is writing
**dispositive prose that does not contradict itself** — the exact task where the `Efficiency` die
amplifies contradiction-following failures. A wizard that emits a subtly self-contradictory prompt is
worse than no wizard, because the client has no instrument to notice. Alembic runs once, at
onboarding, not per conversational turn: this is the wrong place to save.

Alembic is not a `MorganaAgent`, so it carries no `[RequiresLLMTier]`; it consumes
`ILLMService.GetChatClient(Performance)` directly. Consequence, deliberate and inherited from the
framework's own no-cross-tier-fallback rule: **Alembic does not serve a single-tier deployment**
(Ollama being the canonical case) until a `Performance` entry is configured.

### Alembic is of Morgana, and so is everything it writes

Alembic is an agent of Morgana that produces agents of Morgana. It is therefore composed the way one
is — layered, fenced, subordinate to her — from an `alembic.json` of **identical shape** to an
agent's (Target / Instructions / Personality / Formatting), embedded the way `morgana.json` is
embedded in Morgana.AI. Dogfooding: whoever tunes Alembic does the job Alembic teaches.

Two layers, in `AlembicPromptService.ComposeAsync`:

1. **Morgana's own Personality**, resolved live from `morgana.json` rather than copied — her
   identity is Alembic's identity, and a copy would drift the day someone tunes her voice.
2. **The pass** — a complete agent prompt: Target, Personality, Instructions, Formatting.

Two, not three. An earlier design put a shared `Doctrine` between them, on the model of Morgana's
own framework layer — but hers is *glue*, binding the global policies to the multi-turn and context
machinery, and there is nothing here for such a layer to bind. **The structure is what carries the
semantics**: the four sections say what they always say, and a pass that is an agent prompt needs no
scaffolding around it. What the three passes state identically they each state themselves, exactly
as two agents in `agents.json` each state their own read-only rule.

That collapse halved the composed prompt, from ~10 000 characters to ~5 000, and everything cut was
scaffolding: role taxonomies, restated rationale, sentences describing rather than instructing. The
prose an interviewer reads obeys the same law as the prose it writes — clear, direct, committed to
the purpose, focused on how to reach it.

What is deliberately left out of layer 1: her `GlobalPolicies`, her `Formatting` and her `Target`.
Those govern how a **channel turn** is formed — quick replies, rich cards, turn continuation, the
system tools every agent shares, markdown for a rendered surface — and Alembic has no channel, no
Guard, no Classifier and no turn in that sense. Handing it rules about things that
do not exist in its world is the most direct way to manufacture the non-local contradictions this
project exists to avoid. **What carries over is who she is; what does not is the mechanics of a
conversation Alembic is not having.** For the same reason the variant "Alembic is a routed Morgana
agent with its own intent" was rejected: it would drag in the whole Akka pipeline for an interview
that has nothing in common with it.

### The four sections, and staying inside the universe

The doctrine gives each section one question to answer, because a sentence in the wrong section is
worse than a sentence missing — the agent reads each for a different purpose:

| Section | Answers | Size |
|---|---|---|
| `Target` | what this agent does well and existentially, and what it is significant to say it does **not** do | 2–4 sentences |
| `Instructions` | how it goes about it, what it is trying to achieve on the way, what it must **not** do while doing it | 2–5 sentences |
| `Personality` | the empathy, language, tone and humanity it meets the user with — voice only | 2–3 sentences |
| `Formatting` | how this agent presents its **own** information: which shape suits which tool's output | brief, concrete |

`Instructions` and `Formatting` both speak about the toolkit, which is exactly why they wait for the
toolkit pass.

And the universe is self-consistent by rule, not by luck. An authored agent is **one agent of
Morgana**, never a separate creature: it is never "a virtual assistant", never "a helpful bot",
never neutral corporate staff. `Personality` **names which facet she is here** — "a formal and
exacting witch", "a brisk and steady witch" — which specialises her voice and is new information,
where a bare list of adjectives would leave the agent sounding like anyone. Where the domain admits
it she gets something of her own to speak from (a ledger, a scroll, a seed-bed); where it does not —
someone whose pet is ill does not want whimsy — she gets none, and the brevity is the character. The
colouring always stays in **how she speaks, never in what she claims to do**: a ledger may be gazed
into, an invoice may not be conjured.

### Regeneration contract

Generated C# is split across two files per class:

| File | Owner | Rule |
|---|---|---|
| `X.g.cs` | Alembic | attributes, constructor, `partial` signatures — **always overwrite** |
| `X.cs` | the client | the working mock body, then the client's real integration — **written once, never touched again** |

The split does double duty: it is the non-destructive-regeneration mechanism, *and* it is the line
between what is templated (deterministic, so a re-run produces no spurious diff) and what is authored
by the LLM (a plausible domain mock, which a template writes badly).

The agent class has no client half: a `MorganaAgent` subclass is attributes plus one constructor
handing its type to `MorganaAgentAdapter`, and every domain decision it expresses is configuration
Alembic already holds. A tool class does, and the seam is `partial` **methods**: the `.g.cs` declares
`public partial Task<string> GetInvoices(string userId, string count);` from the same
`ToolDefinition` that goes into `agents.json`, and the `.cs` implements it. That buys two things for
free — the pair `MorganaToolAdapter.AddTool` validates at startup is generated correct by
construction, and a tool added to the configuration and forgotten in the code **does not compile**.

Every emitted parameter is a `string`, and that is a statement about the configuration rather than a
shortcut: `Records.ToolParameter` carries a name, a description, required, scope and shared, and no
type. The JSON schema the model reads is generated from the *delegate*, so the type lives in the C#
and only there. Narrowing one is a one-word edit in both halves; guessing one from a parameter's name
would be a guess the client meets at runtime.

Because Alembic never sees the client's tree, this convention travels **inside the downloaded
archive** and holds even where Alembic is absent. It does not need enforcing either: a signature that
drifts is already a startup failure, since `MorganaToolAdapter.AddTool` validates delegate against
definition. Alembic's job is only to surface it earlier, via an unconditional **migration report** of
what changed against the uploaded `agents.json`.

### The emit, and what a template must not write

The archive is one download because the pieces are only correct together: an `agents.json` whose
toolkit has moved on from the C# beside it is a startup failure, and two downloads is an invitation
to take one of them. Inside: the configuration, the generated sources, a working mock per toolkit,
`MIGRATION.md`, `README.md` carrying the two-halves convention, and the interview's save file.

The split between `ICodeEmitService` and `IToolMockService` is the same split the file names carry.
The emit is **string templates and nothing else** — no Roslyn — so the same Draft emits the same
bytes and a re-run diffs to exactly the change. The mocks are the one artifact a template writes
badly: invented invoices, a diary with believable gaps, stock levels that vary. They are **mocks and
not stubs**, and the reason is the turnkey promise — the client drops the archive into a plugin,
starts Morgana, and must be able to *talk to their agent on the first run*, which is the only way to
hear whether the prose the interview wrote is right. A `NotImplementedException` makes the prose
unreviewable, and the prose is what the whole interview was for.

Two things the live run settled. First, **no output ceiling is declared in Alembic's code**: a source
file's length is a property of the toolkit, not a number choosable in advance, so `ToolMockService`
streams and resumes on `FinishReason.Length` until the model stops of its own accord — the only other
exit being a continuation that adds nothing. Second, a reasoning model spends its budget on thinking
**first**: at the tier's prose-calibrated 8192 the InventoryTool request came back with 8192 output
tokens, all reasoning, and *zero characters of text*. Removing `MaxOutputTokens` entirely is worse,
because Anthropic requires `max_tokens` and the SDK default is smaller. The number therefore lives
where a number belongs — Alembic's own `appsettings.json`, generous, with the reason written beside
it — and an empty answer throws rather than writing an empty file, because a source file that looks
like success and is not is the worst thing to hand a client at the end of an interview.

Verified end to end against the shipped `Examples` domain: import → emit → unzip into a class library
referencing `Morgana.AI` → **`dotnet build`, 0 errors**, four agents and three toolkits with 856 lines
of mock behind them.

### The migration report

Unconditional, greenfield included, because a report that only appears when something is wrong is a
report nobody has learned to read on the day it matters. It diffs the Draft against
`DomainDraft.Baseline` — the domain frozen at import, kept as a Draft rather than as the uploaded
bytes so the comparison is like with like, and serialized with the save file so a second sitting does
not lose it. `Provenance` alone could not serve: it says an element was revised, never what it was.

### Two files, one gesture

An interview over a real domain does not fit in one sitting and Alembic has no database, so
`alembic-draft.json` is the whole of its memory: provenance, the C# facts a configuration cannot
express, half-answered elements, and the baseline. Both it and an `agents.json` arrive through the
**same upload control**, because to the client they are one gesture — *here is where I am* — and
which one it is is decided **by reading the file, never by its name**: a serialized Draft carries
`CreatedAt` at the root and a configuration never does. A renamed file still lands where it belongs.

Verified end to end without a model: a save file and a configuration are told apart correctly, a
resumed Draft keeps its provenance, its tiers and its baseline (whose own baseline stays `null` —
one level, never a chain), the migration report still diffs against that baseline on the second day,
and the `agents.json` re-exported after the detour is **byte-identical** to the one before it. The
save file is roughly twice the configuration's size, which is the baseline, and is the price of a
report that does not start lying when the client comes back tomorrow.

Entries are ordered by what they cost to act on, and the load-bearing section is signatures. A tool
whose parameter list changed still compiles on the generated side and fails at Morgana's startup in
`MorganaToolAdapter.AddTool`, and the client-owned half is exactly where that fix is made by hand.
Nothing is applied for the client, and nothing pretends to be: Alembic cannot see their tree, so what
it can do is name every change precisely enough to apply in a minute.

### The recap is the real prompt

The interview recap is **the composed prompt the model will actually read**, not a summary of the
client's answers — a summary would be Alembic grading its own homework. This is possible because
`IPromptComposerService.ComposeAgentInstructionsAsync` takes the domain prompt as a parameter: the
`Records.Prompt` is built in memory from the Draft and needs to exist nowhere on disk.

It is shown as **three separate blocks, never concatenated**, because that is the framework's
placement ladder and each rung is read at a different moment of the turn:

1. the composed two-layer system prompt — read once, before anything else;
2. each tool description as the model weighs it, with `ToolDescriptionContextGuidance` already
   spliced in where the tool declares context-scoped parameters, plus the parameter descriptions
   exactly as authored (the framework splices no template at that rung, and an undescribed
   parameter is emitted bare);
3. the per-turn held-context declaration — **hypothetical by construction**, and labelled as such
   in the UI. That injection states which variables the session holds *right now*, and no session
   exists while authoring. The template and the splice are real; the supposition is that every
   context-scoped parameter in the toolkit happens to be populated at once.

**What the recap is true of.** The framework layer comes from the `morgana.json` embedded in the
Morgana.AI this Alembic was built against, and the framework offers no override for it anywhere —
it is an embedded resource, so "customising the policies" means forking Morgana.AI. The recap is
therefore true for that version of Morgana and says nothing about a different one. Alembic
deliberately does **not** accept an uploaded `morgana.json`: that would model a capability the
framework does not have.

### Alembic is an agent, and its tools are declared the way an agent's are

Alembic is assembled with the framework's own machinery, not an imitation of it:
`MorganaToolAdapter` binds each tool's declaration in `alembic.json` to its delegate — validating
parameter count, names and required/optional, so a declaration that drifts from its method fails at
assembly rather than reaching the model as a schema nothing can satisfy — and
`IChatClient.AsAIAgent` makes the agent.

Not `MorganaAgent` via `MorganaAgentAdapter`: that belongs to the routed world of `agents.json`,
`[HandlesIntent]`, base tools and per-conversation persistence, and Alembic has none of it. **The
reuse stops exactly where the resemblance does.**

Tools rather than a structured reply, and the difference is not stylistic:

- Alembic simply **talks** to the client and carries the configuration out of band, so the reply
  text and the proposal stop being welded into one object.
- A malformed answer stops costing the client a turn of their own interview — that failure branch
  no longer exists.
- **A tool answers back.** Every method in `InterviewTools` returns a sentence *to the model*: a
  `Target` that arrives at one sentence where the shape is two to four is told so and corrects
  itself in the same turn. Recorded either way — the size is a shape, not a gate.
- **What a pass may write is which tools exist.** The functional pass has no `SetAgentInstructions`
  and no `SetAgentFormatting`, so it cannot write them. The constraint stopped being a sentence
  asking for restraint.
- `SetPassCompleted` is believed only as far as the state machine can confirm it, and the
  declaration lives in the call rather than in a token inside the prose — the same out-of-band rule
  Morgana applies to `SetTurnContinuation`, for the same reason.

Three of the tools are the ones that stop Alembic writing blind: `GetExistingIntents` (the
descriptions the classifier will weigh this one against — an overlap you never looked at is a
collision no prose fixes afterwards), `GetFindings` (the deterministic pass, run against a probe
domain so the relational rules are visible, filtered to this pass's business), and
`GetComposedPrompt` (the whole of what the authored agent's model will read). **Alembic is the last
reader of an agent before it exists**, and these are what let it read. `GetToolkit` joins them from
the toolkit pass on, for the same reason at the tool layer: a description that never says *when* to
call the tool is only visible when the toolkit is read back whole.

### Choices: a channel's contract, an interviewer's doctrine

Alembic has no `IChannelService` and needs none — it *is* its own UI. `SetChoices` attaches buttons
to a question, carried by `Morgana.Contracts.QuickReply`, so the shape the client sees in Alembic is
literally the shape their own agents will emit. The component is **not** borrowed from Cauldron:
same contract, own rendering, exactly as Rune and Grimoire relate to it.

The doctrine is nearly the inverse of Morgana's, and that is why copying the component would have
been wrong. Her quick replies let a user pick the next action, and the channel gates the text box
while they are live. Alembic's only ever ask a **closed** question:

- **Never on a question about the client's domain.** Their words are the material being distilled,
  and a menu replaces them with Alembic's — an expert clicking a button you wrote is an expert who
  has stopped telling you how they work.
- Only where the answer set is closed and known to Alembic rather than to the client — in practice
  that is **confirming an inference Alembic has just stated back**. Not the framework's vocabulary
  asked raw: "is this parameter context-scoped?" is a closed question the client cannot answer,
  which is why scope is inferred rather than offered.
- **The text box never closes.** A choice is an offer, never a gate, so the escape is structural
  rather than an extra button. A spent row is shown inert rather than removed: the transcript
  should still say what was offered.

The functional pass rarely uses them, and correctly so — nearly every question it asks is a domain
question. Observed across a full three-pass run: **two turns out of eighteen**, both of them
confirmations, which is the right frequency rather than a shortfall.

Rich cards have no equivalent here and are not missed: the persistent configuration panel beside the
transcript is the better surface. A card is richness *per turn* that scrolls away with the
conversation; the panel updates on every proposal and stays.

### The interview: C# owns the state, the model owns the conducting

The split is fixed. **What has been established, which pass is running and what may be written next
are facts**, and facts are not left to a model's discretion — they live in `InterviewState` and in
`InterviewService`'s merge and readiness gate. **Which question to ask next, and how to turn a
domain expert's answer into dispositive prose, is the model's** — no template writes that well.

Two consequences that look like details and are not:

- Readiness is checked, not believed. The model reports that it thinks a pass is settled; the state
  machine confirms the fields are actually there before agreeing. **And the moment it agrees, the
  next pass opens** — inside the same `AnswerAsync`, with no gesture from the client. Asking them to
  press a button between two passes was asking them to acknowledge a boundary that is Alembic's
  alone, and the only thing they can say about it is yes. `IInterviewService` therefore has no
  `Advance`: the loop terminates because entering a pass clears the flag, and a pass that settles
  the moment it opens (a toolkit the client already described in full) moves on instead of stranding
  the interview one question short.
- The **section labels** (`[TARGET]`, `[PERSONALITY]`, `[INSTRUCTIONS]`, `[FORMATTING]`) are
  guaranteed in code, not asked of the
  model. Both composed layers use the same four labels — precisely why the framework fences them —
  so a domain layer arriving unlabelled leaves half the composed prompt without the markers the
  other half has. A label says *which section this is*, not what it means: it is structure, and a
  structural invariant must not depend on a model remembering a formatting rule. The normalisation
  is idempotent and applies only to prose the interview authored; an imported agent's prose is the
  client's and is never rewritten.

The client never writes prose and is never shown a field name. They answer questions about their
work; Alembic writes the configuration and says what it understood. That asymmetry is the whole
arrangement, and it is what spares a domain expert from having to become a prompt author.

**One question is the whole screen**, and that is the same argument made in layout. The earlier page
put a scrolling transcript beside a filling panel, which reads as a long single page of requirements
gathering — and a domain expert answers a long page of requirements the way anybody does, by getting
shorter. So the question stands alone and large, everything said so far folds into a line nobody has
to open, and what the answers have become runs along the foot as a strip of drops, lit where the
last exchange moved one. The rail of four stations (*what it is*, *what it reaches for*, *how it
works*, *in the domain*) reports where the distillation has got to and controls nothing: three of
them are the model's to leave, and only the last is the client's to take.

The client will also never *use* the agent — the people who will are their customers — so everything
Alembic writes is addressed to the agent about those people, never to the client. Two sentences in
the doctrine, because it is a pronoun that needs pinning down and not a taxonomy that needs
teaching.

### Three passes, three agents

The passes are the machine's states, and their order is forced by an inverse dependency: an agent's
`Instructions` and `Formatting` speak about its tools, so they cannot be written before the toolkit
exists.

| Pass | Settles | Cannot touch |
|---|---|---|
| `Functional` | the intent's four fields, the agent's `Target` and `Personality` | tools, `Instructions`, `Formatting` |
| `Toolkit` | the tools, their descriptions, their parameters, scopes and sharing | what the agent *is*, `Instructions`, `Formatting` |
| `Return` | `Instructions` and `Formatting` | everything already settled |

Each pass is a **fresh agent and a fresh session**, and that is design rather than limitation: a
toolkit pass carrying the whole functional interview in its context spends it re-litigating
decisions already taken, and pays for that context on every turn thereafter. What crosses a pass
boundary is the *configuration*, handed over by `GetAgentSoFar` and `GetToolkit` — read as settled
fact rather than replayed as a conversation. The client's transcript is continuous: they are having
one interview, and the seam belongs to the model alone.

The right-hand column is enforced structurally. A pass's toolset is its own `Tools` declaration in
`alembic.json`, so the toolkit pass has no `SetIntent` and no `SetAgentTarget`, and the functional
pass has no `DeclareTool`. `InterviewState.Missing()` is pass-scoped for the same reason — a pass is
complete when the fields *it* owns are set, and the toolkit pass owns none, since an agent with no
native tools is the legal MCP-only shape.

### One question, one step

The characteristic failure of an LLM-conducted interview is not a wrong answer, it is **circling**:
the model asks the same thing a second and a third time, each phrasing slightly better than the
last, and the client stops answering. It appeared first in the functional pass on an agent's
boundaries, and again in the toolkit pass — where the shape of the work invites it, because the
naive reading asks each parameter's scope separately and four tools carry a dozen parameters.

The fix is doctrine, high in Alembic's own `Instructions` where all three passes read it, rather
than a patch per pass: every question is a step, and an answer advances it if **anything can be
written down**. Adequate is not complete and never ideal — half an answer advances the step, and the
other half is inferred, proposed, and corrected rather than asked again. Asking twice tells the
client their answer was not good enough; asking three times ends the interview whether they say so
or not.

The toolkit pass then states the one instance the doctrine cannot know: **scope is inferred, never
asked per parameter.** The client is asked once, about their setup — what the system already knows
about a user the moment they arrive — and everything on that answer is `context` for the whole
toolkit while everything else is `request`. `Shared` is an inference from what the value *is*: an
identity the domain establishes once is shared, an agent's own working value is not.

### Validation runs before the recap

The order is the design. Composing a beautiful prompt for a domain that would not start is a way of
lying to the client with something that looks like evidence.

Every check in `DraftValidationService` is decidable by reading the Draft — no model is asked, and
none would help. Most of them restate a rule the framework already enforces at startup, and the
duplication is the entire value: an `InvalidOperationException` from
`HandlesIntentAgentRegistryService` arrives after the client has packaged, deployed and run,
whereas the same sentence here arrives while they are still authoring and it costs nothing to
change. Each finding carries a `Because` naming the rule, so it teaches instead of merely refusing.

What it cannot see matters as much: whether two intent descriptions overlap enough to collide in
the classifier, or whether an agent's `Instructions` contradict its `Formatting`, is not decidable
here. That is the cross-agent coherence pass, it needs a model, and nothing in this service guesses
at it.

Against the shipped `Examples` domain the pass reports **0 errors and 5 warnings**, all of them
true statements about an *imported* domain: four agents whose class names are Alembic's guess and
whose tier is unknown, and one agent with no native tools (legal — `Monkeys` is MCP-only).

### The starter scenarios: templates in, one domain out

A domain agent *is* its prose, prose gets edited, and the only way to know an edit broke nothing is
PromptHarness. A client who leaves without scenarios has a domain nobody can revise safely. Alembic
writes the **starting set and no more**: it knows what the agents were designed to do, which is what
a first scenario is made of, and nothing about what will actually go wrong, which is every scenario
after it.

The split that makes this work is between **which behaviours are worth protecting** and **which
words say them here**. The first is knowledge about agents, true before any client arrives, and it is
settled once in this repository as `Harness/Templates/*.yaml`: one file per behavioural use-case,
each a scenario the harness would load if its placeholders were real. The second is knowledge about
the client's business, and only a model that has just read the whole domain can supply it. So the
model derives — it replaces every `{{…}}` with this domain's own words and changes nothing else.

Asking a model for "two or three scenarios" was the earlier shape and was the wrong one: it made the
model choose which behaviours matter, which is the one decision it is worst placed to take after a
single request, and the answer was the same three shapes every time.

| Template | Protects | Needs |
|---|---|---|
| `capability-happy-path` | the flow the agent exists for, end to end | a tool |
| `prerequisite-before-action` | it asks for what it needs instead of inventing it | a tool |
| `confirmation-before-commit` | nothing irreversible happens before a yes | a tool |
| `boundary-refusal` | the edge its own `Target` commits it not to cross | — |
| `tool-choice-under-ambiguity` | the request between two tools reaches the right one | two tools |
| `absent-subject` | it says nothing was found instead of writing something plausible | a tool |
| `withheld-detail` | what its `Formatting` keeps back stays back | — |
| `established-context-not-reasked` | a value given once is not asked for twice | a context parameter |

The right-hand column is the only applicability decided in C#, because counting a list is not a
question worth paying a model for. Everything semantic is the model's: a template it has no instance
of comes back `not-applicable:` with a reason and is dropped. **A read-only toolkit has no
confirmation to protect, and a scenario demanding one would fail a correct agent every run** — which
is why declining had to be a first-class answer, and why each template is asked about on its own.

### What the templates are not

They are **not copies of anything**. Nothing is linked or embedded from `PromptHarness/`: that suite
is entirely infrastructural — it *is* the framework half — so there is nothing in it to derive a
domain scenario from, and a worked example taken from it teaches the subject along with the form.
The templates carry the harness's shape because they were written against it, not because they are
pieces of it.

**And that is what makes the suite 100% domain, structurally rather than by instruction.** The
earlier design said so in prose and hoped; now the vocabulary a derivation is allowed is exactly the
union of the keys the templates use — fourteen, all of them domain — so `guardCompliant`,
`textMaxLength`, `summarizationOccurred`, `textNotMarkdown`, `degradedChannel`, `classifierIntent`
and the whole context cycle are not reachable. They are Morgana's, her scenarios cover them, and
they are maintained where the policies are. A client's copy of a policy scenario drifts from the
policy while the original does not.

The templates also carry what no key table conveys: `runs: 3, minPasses: 3` is already written in
with the comment saying why it is a contract and not a tendency, and the header tells the model what
a good derivation of *this* use-case decides. The reasoning is in the artifact rather than in a
briefing beside it.

### The one check, and why it cannot wait

**A derivation may drop a key and may never add one.** That is the whole rule, and it is enough
because the template is the vocabulary — every key in it is one Alembic wrote knowing the harness
binds it.

It has to be checked at the emit because it cannot be caught later. `ScenarioLoader`'s deserializer
is built `.IgnoreUnmatchedProperties()` — right where it is, since a suite that refused to load over
one stray key would be brittle in the hands of the person editing it — which means **a key the
harness does not recognise is dropped without a sound**: the scenario loads, runs, passes, and
asserts nothing. It reads as coverage and is not, and a model reaching for a plausible-but-absent key
(`textContains`, say) produces exactly that. So every derivation is parsed with YamlDotNet and held
to its template's key set, along with two other silent failures: a placeholder left unresolved, and a
document with no turns.

A scenario that fails still ships, with the problem written across its top. Alembic has no business
deciding a client may not see its own artifact, and a silently missing scenario costs them more than
a visibly broken one. The `id` is Alembic's either way — `{intent}-{template}`, unique across a
domain by construction — rewritten as a line rather than re-serialized, because re-serializing would
discard the comments and a comment saying why a turn admits two shapes is how the next reader knows
the looseness was deliberate.

### The discretionary pass

The templated base protects what is worth protecting in any domain, which is precisely the part that
could be known in advance. It cannot protect the rule that holds only here — the step that must
never happen twice, the two things this business never says in the same breath — because nobody knew
about it until the interview was over.

So one call runs after the base, handed the domain and **the base in full**, and may add up to two
scenarios of its own. The base is handed over whole for the one constraint that matters: an extra
scenario must neither repeat what is already asserted nor contradict it, since a suite where two
files disagree about the same turn is worse than one that never covered it — the failing one gets
deleted and nobody remembers which was right. It is held to the union vocabulary rather than to one
template's keys, and **none is the ordinary answer**, stated as such in the request: a domain the
base already describes is a well-designed domain, not a gap.

### The coherence pass

The other half of reviewing a domain, and the half `DraftValidationService` explicitly cannot do.
That one decides everything decidable by reading the Draft and asks no model, because none would
help. Whether two intent descriptions overlap enough to collide in the classifier is the opposite
kind of question: it is about meaning, and it is the most expensive defect a multi-agent domain
carries, because no prose downstream resolves it and the user meets it as an agent answering the
wrong question.

Every defect it looks for is **relational**, which is exactly why nothing earlier could see it — the
interview settles one agent at a time by construction. Overlapping intents; two agents claiming the
same capability; a value one publishes as `userId` that another expects as `customerCode`; ground
the domain's own descriptions imply and no agent covers; an agent promising what no tool of its
backs. It is handed the domain's exact words, never a summary, because a summary is precisely the
step that would smooth an overlap away before the model saw it.

It answers JSON — the one place in Alembic that does, because this output is a list to be sorted and
tabulated rather than writing to be read. And it **advises, never blocks**: a domain expert who
disagrees with it about their own business is usually right.

## Project Structure

```
Alembic/
  Alembic.slnx                        # Solution (sibling of Morgana.slnx, Cauldron.slnx, …)
  Alembic.csproj                      # .NET 10 Web SDK, ProjectReference → Morgana.AI
  Directory.Build.props               # Build settings, version, package metadata
  Directory.Build.targets             # Regenerates the root .env.versions on each build
  Alembic.Dockerfile                  # Multi-stage container build (root context)
  appsettings.json                    # Morgana:LLM section (Performance tier only)
  alembic.json                        # Alembic's OWN prose AND tool declarations, embedded resource
  Properties/launchSettings.json      # Dev profile: https://localhost:5005
  Program.cs                          # DI wiring and app pipeline
  App.razor                           # Blazor root component
  _Imports.razor                      # Shared @using directives
  Model/
    DomainDraft.cs                    # The Draft: DomainDraft, IntentDraft, AgentDraft, ToolDraft, …
    AgentsConfigurationFile.cs        # The on-disk shape of an agents.json (Morgana's own Records inside)
    DraftProjection.cs                # Draft → Records, shared by the exporter and the recap
    ValidationFinding.cs              # Severity, Where, Message, Because
    AgentRecap.cs                     # The three rungs of the placement ladder
    InterviewState.cs                 # The interview's C# state machine
    Provenance.cs                     # Imported / Revised / Authored
  Interfaces/
    IDraftImportService.cs            # Uploaded agents.json → Draft
    IDraftExportService.cs            # Draft → agents.json (the round-trip invariant)
    IDraftSerializationService.cs     # Draft ⇄ alembic-draft.json (the interview's save file)
    IDraftValidationService.cs        # Everything decidable about a Draft without a model
    IRecapService.cs                  # Draft → the prompt the model really reads
    IAlembicPromptService.cs          # Alembic's own prose + tool declarations, from alembic.json
    IInterviewService.cs              # Conducts a pass, folds the result into the Draft
    IDraftStateService.cs             # The Draft under construction (per circuit)
  Harness/                            # Alembic's own harness component — owes PromptHarness nothing
    Templates/*.yaml                  # One behavioural use-case each, domain words left as placeholders
    ScenarioTemplate.cs               # A template: its `#@` brief and the scenario shape below it
    ScenarioTemplateLibrary.cs        # The templates, embedded; which apply; the union vocabulary
    ScenarioDerivation.cs             # A derivation checked against the vocabulary it was allowed
  Services/                           # Default implementations of the above
    InterviewTools.cs                 # The tools Alembic calls while conducting a pass
  Pages/_Host.cshtml                  # Blazor Server host page (ServerPrerendered)
  Pages/Index.razor                   # Landing: the vessel, and four cards for the four things to do
  Pages/Import.razor                  # Upload an agents.json, see the parsed Draft, download it back
  Pages/Review.razor                  # Findings, then the composed prompts
  Pages/Interview.razor               # The wizard: one question a screen, a rail of stations, drops at the foot
  Shared/MainLayout.razor             # Layout wrapper
  wwwroot/css/site.css                # The whole surface. Palette read off the alembic image
  wwwroot/favicon.svg                 # The vessel at 16px: belly, neck, spout, one spark
  wwwroot/images/alembic.jpg          # Morgana's alembic — the landing's centrepiece
```

## The Draft

The single artifact the interview fills, the validator checks, the recap composes and the emit
reads. Three things about it are decisions rather than mechanics:

**Why not the `Records` types directly.** They are the *serialization* model: immutable, complete,
positional. The Draft is the *editing* model, and an interview in progress is incomplete by
definition — a tool whose description has not been asked for is a different state from one whose
description is deliberately empty, and only a nullable field distinguishes them. Every nullable
string in `DomainDraft.cs` means "not asked yet". Where a shape is final it still *is* the
framework's own record; nothing here re-models a concept Morgana already models.

**What survives that Alembic does not understand.** AdditionalProperties keys other than `Tools`
are kept verbatim in `AgentDraft.UnmodelledProperties` and written back untouched. The round-trip
invariant must not depend on Alembic having a use for every key it meets. The `Tools` key is
matched **ordinally**, deliberately: `Records.Prompt.GetAdditionalProperty` looks it up in a plain
`Dictionary<string, object>`, so a differently-cased key is invisible to the framework and must
stay invisible here rather than be promoted into a toolkit Morgana would never load.

**Provenance** (`Imported` / `Revised` / `Authored`) exists so Alembic rewrites only what it owns
and can *report* honestly. It is not what preserves untouched content — that is the round-trip
invariant, which holds regardless.

## The round-trip invariant

**A configuration that goes in comes back out equivalent.** This is what makes the interview safe
to build on: a client uploading a domain of ten agents to add an eleventh gets the other ten back
untouched, and Alembic does not need to understand them to promise it.

Equivalent, not byte-identical, and the difference is not a compromise — it is what the format
actually means:

- `AdditionalProperties` is a *list* of dictionaries and Morgana looks keys up **across** every
  entry, so the grouping carries no information. The exporter writes the toolkit as its own entry
  followed by the unmodelled ones, which need not reproduce the arrangement the file arrived with.
- Defaults are written explicitly. An omitted `Shared` and an explicit `"Shared": false` say the
  same thing; stating it is the clearer of the two.
- Emoji come back as escaped surrogate pairs. `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` stops
  escaping accented text, dashes and apostrophes, but its allow-list is expressed in
  `UnicodeRange`s, which stop at U+FFFF — so anything outside the BMP is still escaped. The first
  export normalises a hand-written file once; every export after that is diffable against the one
  before it, which is the comparison that matters in use.

Verified against `Examples/agents.json` (5 intents, 4 agents, 14 tools, 23 parameters): the
exported JSON is semantically equal to the original field by field, re-importing it yields an
identical Draft, and exporting that Draft again is byte-for-byte stable — the export is a fixed
point, so a file that has been through Alembic once stops moving.

`AgentCodeFacts` holds what `agents.json` cannot: namespace, class names, tier, MCP servers. On
import all of it is unknown, so Alembic proposes the class names from the framework's naming
convention and flags the whole record `Inferred`. Namespace and tier are left null rather than
guessed — they follow from nothing in the file, and a confident wrong value is worse than an empty
one the interview will ask about. The inference is a genuine guess and is meant to be seen as one:
against the `Examples` domain it proposes `MonkeysAgent` where the real class is `MonkeyAgent`.

## DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `ILogger` | Singleton | Several Morgana.AI services take a bare `ILogger`, not `ILogger<T>` |
| `IAgentConfigurationService` | Singleton | `EmbeddedAgentConfigurationService` — finds no embedded `agents.json` and degrades to agentless mode, which is the correct state: the domain Alembic works on is the **uploaded** one, never one compiled into this process |
| `IPromptResolverService` | Singleton | `ConfigurationPromptResolverService` — resolves the framework prompts from `morgana.json`, embedded in Morgana.AI and free through the project reference |
| `IPromptComposerService` | Singleton | `ConfigurationPromptComposerService` — assembles what the model reads; this is what makes the recap a real composed prompt |
| `ILLMService` | Singleton (factory) | Provider selected by `Morgana:LLM:Provider`. **Never resolved during startup**, so a working copy without credentials still builds, boots and serves the shell — the failure surfaces on the first call, with the provider's own message |
| `IDraftImportService` | Singleton | `DraftImportService` — uploaded `agents.json` → Draft. Holds no per-client state; it only projects one shape onto another |
| `IDraftExportService` | Singleton | `DraftExportService` — Draft → `agents.json`. Rebuilds Morgana's own record types and serializes those, so the file Alembic emits and the file Morgana reads are the same type seen from two sides |
| `IDraftSerializationService` | Singleton | `DraftSerializationService` — Draft ⇄ `alembic-draft.json`. An interview over a real domain does not fit in one sitting, and Alembic has no database |
| `IDraftValidationService` | Singleton | `DraftValidationService` — the deterministic checks, each restating a rule the framework enforces later and more expensively |
| `IRecapService` | Singleton | `RecapService` — drives `IPromptComposerService` over the Draft. Deliberately almost empty: anything Alembic added on top would be a claim about the prompt rather than the prompt |
| `IAlembicPromptService` | Singleton | `AlembicPromptService` — loads `alembic.json` from this assembly, the way `ConfigurationPromptResolverService` loads `morgana.json`. Unlike it, refuses to degrade to an empty set: an interviewer with no prose is not diminished, it is silent |
| `IInterviewService` | **Scoped** | `InterviewService` — one interview per circuit. Builds a Microsoft.Agents.AI agent via `MorganaToolAdapter` + `AsAIAgent`, on `GetChatClient(Performance)` directly rather than `CompleteWithSystemPromptAsync`, which always takes the cheapest tier |
| `IDraftStateService` | **Scoped** | `DraftStateService` — the Draft under construction. One per Blazor circuit: two tabs are two separate interviews, and the state dies with the connection |

## Why the project reference is Morgana.AI, not Morgana.Contracts

Cauldron, Grimoire and Rune reference `Morgana.Contracts` because they exchange wire DTOs. Alembic
exchanges none. What it needs is the **domain model of a Morgana configuration** — `Records.Prompt`,
`Records.ToolDefinition`, `Records.ToolParameter`, `Records.Intent` — plus `IPromptComposerService`.
Parsing an uploaded `agents.json` is therefore free: there is no parallel representation to maintain.
`Morgana.Contracts` arrives transitively.

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Morgana:LLM:Provider` | `Anthropic`, `AzureOpenAI`, `Ollama`, `OpenAI` |
| `Morgana:LLM:{Provider}:Tiers:Performance` | `Options` (`ModelId`, `MaxOutputTokens`) + `MagicDust`. Only `Performance` is declared — Alembic never uses the Efficiency die. `MagicDust` is present because `Records.TierDefinition` requires it, not because Alembic meters anything: there is no conversation here to charge a budget to |

The section is named `Morgana:` and not `Alembic:` because `MorganaLLM` reads that path. In-repo,
Alembic declares the **same `UserSecretsId` as Morgana.Web** (the same trick PromptHarness uses), so
it runs against whatever provider and tiers this working copy is already wired to, with nothing to
configure twice. A standalone deployment supplies the section by environment variable.

## Build and Run

- **Target**: .NET 10, Blazor Server
- **Build**: `dotnet build` from `Alembic/`
- **Run**: `dotnet run` — default https://localhost:5005. Needs **no** Morgana instance running
- **Docker**: profile-gated, so `compose up` skips it —
  `docker compose --env-file .env --env-file .env.versions --profile authoring up alembic`

## Roadmap

Eight complementary phases. The ordering principle is **deterministic ends first, the LLM in the
middle last**: import → Draft → export is verifiable without a single model call, and once that loop
closes it is a hard invariant the interview cannot break.

| Phase | Content | State |
|---|---|---|
| 1 | Scaffold — solution, project, Blazor shell, Morgana.AI reference, LLM wiring, Docker | **done** |
| 2 | Draft model + import of an uploaded `agents.json` (fixture: `Examples/agents.json`) | **done** |
| 3 | Export + round-trip invariant (an untouched file comes back out intact) | **done** |
| 4 | Deterministic validation + recap as the real composed prompt — *first shippable milestone: useful without any interview* | **done** |
| 5 | Interview, functional pass (`alembic.json`, FSM, intent + agent prose) | **done** |
| 6 | Interview, toolkit pass + return pass (`Instructions`/`Formatting` speak about tools, so they come last) | **done** |
| 7 | C# asset emit + migration report — *turnkey* | **done** |
| 8 | PromptHarness starter scenarios + cross-agent coherence pass | **done** |

## Conventions

- Behavioral concerns behind interfaces, default implementation alongside, DI registration in
  `Program.cs` — same pattern as the framework
- No parallel representation of a Morgana configuration: the `Records` types are the model
- Generated C# obeys the `X.g.cs` / `X.cs` split described above
- Alembic never writes to, reads from, or assumes anything about the client's filesystem
