# Alembic — Morgana's Authoring Workbench

## What is Alembic

Alembic is a **Blazor Server** web application that gives a client the *initial morganization*
turnkey: an AI-conducted functional interview that distils a domain expert's answers into a complete
Morgana domain — intents, agent prose, tool contracts, C# assets and non-regression scenarios.

The name is the point. Every other unit in the repo names an instrument (Cauldron the vessel,
Grimoire the book, Rune the mark, PromptHarness the rig); an *alembic* is the apparatus that
distils, which is what this does to a rambling interview.

Alembic lives at `Alembic/` in the repo root, alongside `Morgana/`, `Channels/`, `Examples/` and
`PromptHarness/` and is itself a container for two projects, each with its own solution:
`Distiller/`, the workbench described throughout this document (`Distiller.slnx`) and `PromptHarness/`,
its own non-regression harness (`PromptHarness.slnx`) — nested here for tidiness but deliberately not
a client of Distiller's own code.

## What Alembic is not

- **Not a channel.** It never calls a Morgana instance, holds no JWT, announces no `ChannelMetadata`,
  joins no conversation pipeline. It is the only unit in the repo that is not a client of a running
  Morgana. Its sole external dependency is an LLM.
- **Not a filesystem tool.** At runtime Alembic lives wherever the client deployed it — a cloud, an
  on-prem box next to Morgana, a laptop — exactly like Cauldron. So it makes **no assumption of
  seeing the client's filesystem**: configuration arrives as an **upload** and leaves as a
  **download**.

That second point is load-bearing: Alembic cannot know which C# already exists on the client's side,
so it never tries to guess, patch or merge it. See *Regeneration contract* below for what replaces
that.

## Design decisions

### Performance tier, non-negotiable

Alembic runs on `Records.LLMTier.Performance`, not out of caution. Its whole job is writing
**dispositive prose that does not contradict itself** — the exact task where the `Efficiency` die
amplifies contradiction-following failures. A wizard that emits a subtly self-contradictory prompt is
worse than no wizard, because the client has no instrument to notice and Alembic runs once at
onboarding, not per conversational turn: the wrong place to save.

It is not a `MorganaAgent`, so it carries no `[RequiresLLMTier]`; it consumes
`ILLMService.GetChatClient(Performance)` directly. Consequence, inherited from the framework's own
no-cross-tier-fallback rule: **Alembic does not serve a single-tier deployment** (Ollama being the
canonical case) until a `Performance` entry is configured.

### Alembic is of Morgana and so is everything it writes

Alembic is an agent of Morgana that produces agents of Morgana, so it is composed the way one is —
layered, fenced, subordinate to her — from an `alembic.json` of **identical shape** to an agent's
(Target / Instructions / Personality / Formatting), embedded the way `morgana.json` is embedded in
Morgana.AI. Dogfooding: whoever tunes Alembic does the job Alembic teaches.

Two layers, in `AlembicPromptService.ComposeAsync`:

1. **Morgana in her own words**, resolved live from `morgana.json` rather than copied: her
   `Personality`, because her identity is Alembic's identity; her `Target`, the only place that says
   what an agent *of* Morgana is; and her `GlobalPolicies` **by name only**, as the subjects already
   settled above every agent.
2. **Alembic's own prose**, from two rows of `alembic.json`: what every pass says identically and
   what this pass adds.

Two, not three: an earlier shared `Doctrine` layer, on the model of Morgana's own framework glue, had
nothing to bind — hers binds global policies to turn/context machinery Alembic does not have. **The
structure carries the semantics**: the four sections say what they always say.

**The second layer is stored deduplicated, read as one.** The six passes differ only in which tools
they hold, so four near-identical copies of the conducting rules, voice and output format were four
places to edit one rule — 22 000 characters, half duplication, one copy already drifted from the
voice it shared. The identical half now lives once, in the `Interview` prompt; a pass carries only
what is its own and `ComposeAsync` merges them **section by section under one set of labels** so the
model still reads the same four sections with no seam. `Personality` and `Formatting` are shared
outright: the interviewer is one person however many passes they conduct.

**A third row says which of two jobs the step is doing: `Composing` or `Correcting`.** Writing a
section that doesn't exist and correcting one that does are different work. Written as clauses inside
shared prose, both jobs sat in every pass and the model read both every time — which is how it twice
opened a fully written agent as though blank, a concrete "start over" procedure beating an abstract
"don't." Written as two whole files they would have reopened the same duplication above; a row costs
neither. The `Correcting` row's load-bearing sentence — *where your own step's instructions describe
building this section from nothing, they describe the other job; read them for what the section IS,
never as a running order to start over with* — lets each pass keep its own procedure written plainly,
with no conditional in it.

**Her `Target` was missing for a long time and that was a real hole**: only her `Personality` went
in, so the interview rested on the model already knowing what "a Morgana domain" means. Alembic
explaining Morgana in its own words would drift the day the framework is tuned, so her `Target` is
injected instead and `alembic.json` states only what `morgana.json` does **not** — that a classifier
routes on intent descriptions, that a tool is the only reach outside the conversation, what
`context`/`request`/shared mean and that `other` catches what nothing else does. Her policies go in
**as names, not bodies**: Alembic needs only *which* subjects are already covered above an agent, not
how.

What stays out: the policies' bodies and her `Formatting`, which govern a **channel turn** (quick
replies, rich cards, markdown for a rendered surface) Alembic doesn't have — handing it rules about
things that don't exist in its world is the most direct way to manufacture the non-local
contradictions this project exists to avoid.

### The four sections and staying inside the universe

Each section answers one question, because a sentence in the wrong section is worse than a sentence
missing — the agent reads each for a different purpose:

| Section | Answers | Size |
|---|---|---|
| `Target` | what this agent does well and existentially and what it is significant to say it does **not** do | 2–4 sentences |
| `Instructions` | how it goes about it, what it is trying to achieve on the way, what it must **not** do while doing it | 2–5 sentences |
| `Personality` | the empathy, language, tone and humanity it meets the user with — voice only | 2–3 sentences |
| `Formatting` | how this agent presents its **own** information: which shape suits which tool's output | brief, concrete |

`Instructions` and `Formatting` both speak about the toolkit, which is why they wait for the toolkit
pass.

The universe stays self-consistent by rule, not by luck. An authored agent is **one agent of
Morgana**, never a separate creature — never "a virtual assistant" or neutral corporate staff.
`Personality` **names which facet she is here** ("a formal and exacting witch"), specialising her
voice where a bare adjective list would leave the agent sounding like anyone; where the domain admits
it she gets something of her own to speak from (a ledger, a scroll) and where it doesn't — an ill
pet wants no whimsy — she gets none. The colouring always stays in **how she speaks, never in what
she claims to do**: a ledger may be gazed into, an invoice may not be conjured.

### Regeneration contract

Generated C# is split across two files per class:

| File | Owner | Rule |
|---|---|---|
| `X.g.cs` | Alembic | attributes, constructor, `partial` signatures — **always overwrite** |
| `X.cs` | the client | the working mock body, then the client's real integration — **written once, never touched again** |

The split does double duty: it is the non-destructive-regeneration mechanism, *and* the line between
what is templated (deterministic, so a re-run produces no spurious diff) and what is authored by the
LLM (a plausible domain mock, which a template writes badly).

The agent class has no client half — a `MorganaAgent` subclass is attributes plus one constructor
handing its type to `MorganaAgentAdapter` and every domain decision it expresses is configuration
Alembic already holds. A tool class does and the seam is `partial` **methods**: `.g.cs` declares
`public partial Task<string> GetInvoices(string customerCode, string count);` from the same
`ToolDefinition` that goes into `agents.json` and `.cs` implements it. That buys two things free —
the pair `MorganaToolAdapter.AddTool` validates at startup is generated correct by construction and
a tool added to the configuration and forgotten in the code **does not compile**.

Every emitted parameter is a `string`, a statement about the configuration rather than a shortcut:
`Records.ToolParameter` carries a name, a description, required, scope and shared and no type. The
JSON schema the model reads is generated from the *delegate*, so the type lives in the C# and only
there — narrowing one is a one-word edit in both halves, where guessing one from a parameter's name
would be a guess the client meets at runtime.

Because Alembic never sees the client's tree, this convention travels **inside the downloaded
archive** and holds even where Alembic is absent and needs no enforcing either — a drifted signature
is already a startup failure via `MorganaToolAdapter.AddTool`. Alembic's job is only to surface it
earlier, via an unconditional **migration report** of what changed against the uploaded `agents.json`.

### The emit and what a template must not write

The archive is one download because the pieces are only correct together: an `agents.json` whose
toolkit has moved on from the C# beside it is a startup failure and two downloads is an invitation
to take one of them. Inside: a ready `.csproj`/`.slnx`, the configuration, the generated sources, a
working mock per toolkit, `MIGRATION.md`, `README.md` carrying the two-halves convention and the
interview's save file.

`ISolutionEmitService` writes the project and solution, the same string-template discipline as
`ICodeEmitService` applied one level up — without it the archive was loose sources. The `.csproj`
references `Morgana.AI` and `Morgana.Contracts` by `PackageReference`, both pinned to whichever build
this Alembic runs against, read off its own assemblies; `Morgana.Contracts` is named explicitly
rather than left to arrive transitively, since `QuickReply`, `RichCard` and the rest are types a tool
body may want to construct directly.

The split between `ICodeEmitService` and `IToolMockService` is the same split the file names carry.
The emit is **string templates and nothing else** — no Roslyn — so the same Draft emits the same
bytes and a re-run diffs to exactly the change. The mocks are the one artifact a template writes
badly: invented invoices, a diary with believable gaps, stock levels that vary. They are **mocks and
not stubs**, because of the turnkey promise — the client drops the archive into a plugin, starts
Morgana and must be able to *talk to their agent on the first run*, the only way to hear whether the
prose the interview wrote is right. A `NotImplementedException` makes the prose unreviewable.

Two things the live run settled. **No output ceiling is declared in Alembic's code**: a source file's
length is a property of the toolkit, so `ToolMockService` streams and resumes on `FinishReason.Length`
until the model stops of its own accord. And a reasoning model spends its budget on thinking **first**
— at the tier's prose-calibrated 8192 one request came back with 8192 output tokens, all reasoning,
zero characters of text — so the ceiling lives generously in `appsettings.json` rather than being
removed and an empty answer throws rather than writing an empty file that looks like success.

Verified end to end against the shipped `Examples` domain: import → emit → unzip into a class library
referencing `Morgana.AI` → `dotnet build`, 0 errors. The generated `.csproj`/`.slnx` were verified
separately against a real NuGet feed, pinned to the newest published `Morgana.AI`/`Morgana.Contracts`
— restored and built clean.

### The migration report

Unconditional, greenfield included, because a report that only appears when something is wrong is a
report nobody has learned to read on the day it matters. It diffs the Draft against
`DomainDraft.Baseline` — the domain frozen at import, kept as a Draft rather than the uploaded bytes
so the comparison is like with like and serialized with the save file so a second sitting does not
lose it. `Provenance` alone could not serve: it says an element was revised, never what it was.

### Two files, one gesture

An interview over a real domain does not fit in one sitting and Alembic has no database, so
`alembic-draft.json` is the whole of its memory: provenance, the C# facts a configuration cannot
express, half-answered elements, the baseline — and `DomainDraft.Sitting`, the interview itself as it
stood.

**The sitting is what makes closing the tab survivable.** Without it the file held only what had been
accepted into the domain, so a client interrupted mid-agent lost that agent and the map they had
dictated to reach it. What is saved is the configuration in hand, never the conversation: the map,
which entry, which step, what has been written so far. Resuming re-enters that step the way
`BackAsync` does — a fresh agent and a fresh session reading what is there as settled fact — since the
memory of this interview has always been the configuration rather than the transcript.

**The work is written down without being asked.** `InterviewService.Keep` runs at the end of every
`ExchangeAsync`, the turn that *opens* a pass included, so the Draft in the circuit is current from
the first question. `DraftStateService` also takes a snapshot every `Alembic:Work:AutosaveSeconds`,
held nowhere but in the service's own field — a floor under one button, not a resumption mechanism:
nothing is cached against a key or picked back up on the way in, since a workbench that silently
resumed something the client had not asked to resume would be deciding for them what they came to
do. `Save my work` serializes the live Draft on the press and falls back to the snapshot only when
that throws, so the client gets bytes at worst `AutosaveSeconds` behind the screen — the only way
back into work a dead circuit took with it.

**The button hands the bytes over one JS call and reads the file back.** An earlier `data:` URI
holding the whole file in the anchor's href produced the worst bug this project has had: one computed
before the Draft existed downloaded as an empty `{}`, one recomputed every turn sent the whole
configuration on every answer. After the press a receipt now says what is *actually in the bytes*,
parsed back out of the downloaded document rather than reported from the interview's own state,
because the save that handed back an empty file did so while the interview on screen was perfectly
healthy — only the file can say what is in the file. It stands on the row with the answer and the way
back, at their size — page furniture was the wrong genus for the only thing standing between a client
and losing an afternoon.

A save file that carries a sitting changes what the import page says — *carry on where you left off*,
naming the entry mid-way through — and both a save file and a configuration arrive through the
**same upload control**, told apart **by reading the file, never by its name**: a serialized Draft
carries `CreatedAt` at the root and a configuration never does.

Verified end to end without a model: a save file and a configuration are told apart correctly, a
resumed Draft keeps its provenance, tiers and baseline (whose own baseline stays `null`, one level,
never a chain) and the `agents.json` re-exported after the detour is byte-identical to the one
before it.

### The recap is the real prompt

The interview recap is **the composed prompt the model will actually read**, not a summary of the
client's answers — a summary would be Alembic grading its own homework. Possible because
`IPromptComposerService.ComposeAgentInstructionsAsync` takes the domain prompt as a parameter: the
`Records.Prompt` is built in memory from the Draft and needs to exist nowhere on disk. Shown as **two
separate blocks, never concatenated**, since each is read by the model at a different moment of the
turn: the composed two-layer system prompt, read once before anything else; and each tool
description as the model weighs it, with `ToolDescriptionContextGuidance` already spliced in where
relevant.

**What the recap is true of.** The framework layer comes from the `morgana.json` embedded in the
Morgana.AI this Alembic was built against — an embedded resource with no override, so "customising
the policies" means forking Morgana.AI. Alembic deliberately does **not** accept an uploaded
`morgana.json`, since that would model a capability the framework does not have.

### Alembic is an agent and its tools are declared the way an agent's are

Alembic is assembled with the framework's own machinery, not an imitation of it: `MorganaToolAdapter`
binds each tool's declaration in `alembic.json` to its delegate, validating parameter count, names
and required/optional so a drifted declaration fails at assembly rather than reaching the model as a
schema nothing can satisfy and `IChatClient.AsAIAgent` makes the agent. Not `MorganaAgent` via
`MorganaAgentAdapter`, which belongs to the routed world of `agents.json`, `[HandlesIntent]`, base
tools and per-conversation persistence Alembic has none of. **The reuse stops exactly where the
resemblance does.**

Tools rather than a structured reply: Alembic simply **talks** to the client and carries the
configuration out of band, so a malformed answer stops costing the client a turn of their own
interview. **A tool answers back** — every method in `InterviewTools` returns a sentence *to the
model*, so a `Target` arriving as one sentence where the shape wants two to four is told so and
corrects itself in the same turn. **What a pass may write is which tools exist**: the functional pass
has no `SetAgentInstructions`/`SetAgentFormatting`, so the constraint is the absence of a tool rather
than a sentence asking for restraint and `SetPassCompleted` is believed only as far as the state
machine can confirm it.

Three tools stop Alembic writing blind: `GetExistingIntents` (what the classifier will weigh this one
against), `GetFindings` (deterministic checks against a probe domain, filtered to this pass) and
`GetComposedPrompt` (the whole of what the authored agent's model will read) — **Alembic is the last
reader of an agent before it exists**. `GetToolkit` joins them from the toolkit pass on, for the same
reason: a description that never says *when* to call a tool is only visible when the toolkit is read
back whole.

### Choices: a channel's contract, an interviewer's doctrine

Alembic has no `IChannelService` and needs none — it *is* its own UI. `SetChoice` attaches a button to
a question, carried by `Morgana.Contracts.QuickReply`, so the shape the client sees is literally the
shape their own agents will emit. The doctrine is nearly the inverse of Morgana's: her quick replies
let a user pick the next action and gate the text box while live; Alembic's only ever ask a **closed**
question —

- **Never on a question about the client's domain**, since a menu would replace their own words with
  Alembic's.
- Only where the answer set is closed and known to Alembic, which in practice is **the answer that
  adds nothing**: agreement with an inference just stated back, "nothing to add," or, where an
  ordinary question's own wording names a minimal branch ("just X, or Y too"), the button for X in
  the question's own words. Never the framework's raw vocabulary: "is this parameter context-scoped?"
  is closed but unanswerable, which is why scope is inferred rather than offered.
- **The text box never closes** — a choice is an offer, never a gate and a spent row is shown inert
  rather than removed.

The functional pass rarely uses them, correctly so — nearly every question it asks is a domain
question. Where they appear they are **required** and the label answers the question exactly as put:
*That's everything*, *Nothing missing*, *No, nothing like that* — never a label answering a different
question than the one just asked.

**There is exactly one button and what it carries is the answer that adds nothing.** Agreement,
*nothing missing* and a named minimal branch are the same answer wearing different faces — the client
has nothing to contribute this turn, so the button is a **short circuit** that settles it with a press
instead of a round trip nobody learns anything from. A second button would carry the opposite, which
is never an answer: *something's missing* still has to be typed. The count is enforced in the
**signature**: `SetChoice(label, value)` takes two plain strings, so a second button isn't
expressible — the same bound applies to confirmations per step: **one**, since nothing here is
irreversible and every step can be walked back into.

Rich cards have no equivalent and are not missed: the persistent configuration panel beside the
transcript updates on every proposal and stays, where a card is richness *per turn* that scrolls
away.

### The interview: C# owns the state, the model owns the conducting

The split is fixed. **What has been established, which pass is running and what may be written next
are facts**, living in `InterviewState` and `InterviewService`'s merge/readiness gate. **Which
question to ask next and how to turn an answer into dispositive prose, is the model's** — no
template writes that well.

Readiness is **checked, not believed**: the model reports a pass settled, the state machine confirms
the fields are actually there and the moment it agrees the next pass opens inside the same
`AnswerAsync`, no client gesture required — asking them to press a button between two passes would
ask them to acknowledge a boundary that is Alembic's alone. `IInterviewService` has no `Advance` for
this reason; it does have `BackAsync` and the asymmetry is the point (see *One question, one step*).
The **section labels** (`[TARGET]`, `[PERSONALITY]`, `[INSTRUCTIONS]`, `[FORMATTING]`) are guaranteed
in code, never asked of the model — structure that must not depend on a model remembering a
formatting rule — and the normalisation applies only to prose the interview authored; an imported
agent's prose is never rewritten.

The client never writes prose. They answer questions about their work; Alembic writes the
configuration and says what it understood — the asymmetry that spares a domain expert from having to
become a prompt author.

**Every step says where it is before it asks anything**, in one fixed shape — *Agent — Step: what
this step adds to the agent's capabilities* — filled from the agent's name off the map, the step
being settled and what it adds. What earns it is that **the process is Alembic's alone**: the client
sees one question on an otherwise empty screen, so the shared layer teaches the model the *process*
and the opening sentence is where that reaches the client, said in the **second person** and asking
**what happens, never what a word means** — "what a typical customer means by 'good' here" asks the
client to define an abstraction of Alembic's and reaches nobody.

**One section per question, never two** and **said once, never explained twice**: the opening
sentence names the section and what it gives the agent — never what the section *is* — and every
question after it is about their work, in their words, never intent/tool/parameter/scope/context/
domain/configuration, each of which costs a beat of translation and comes back as the client's guess
at Alembic's vocabulary. This is also why the rail can be named `Target`, `Personality`, `Toolkit`,
`Instructions`, `Formatting`: those labels are glanced at, not answered.

**The page is wide, because a wizard is not an article** — a rail of steps, a domain map and a box
for a paragraph do not fit prose's reading measure, so each element sets its own. **One question is
the whole screen**: it stands alone and large and what the answers have become folds away at the
foot behind the agent's name, opened by the same tag that already names it on the map, computed once
by `AgentRows` so the tag's count can never disagree with the rows behind it. There is no transcript —
the agent's own `AgentSession` already remembers the conversation, so a second copy on screen would be
a log to keep in step with one that already exists.

**The rail has the shape of the process and that shape is a loop.** `Domain Mapping`, `Domain
Colleagues` and `Domain Finalization` happen once; the six steps between the first and the last — `Target` through `Agent Acceptance` — repeat
per entry inside a bordered lap, stacked like sheets while entries remain. Acceptance sits **inside**
the lap, since it closes one entry's turn and the next opens on the step after the map — a rail read
left to right cannot jump its own light backwards. The rail is visible from the first question, steps
not yet reached shown faint rather than absent.

**What is in the box is a worked example, on the first question of a step and no other** — a
fifty-word answer somebody else might have given, showing register, length and detail at once. All
but the very first are written by the model itself, through `SetExample`, once it has read the map
and can write in the client's own trade; the map's own opening example stays fixed, since nothing is
yet known about this client and a model there would be inventing a hypothetical domain, differently
and unverifiably, on the one screen that sets the whole interview's register.

**Backwards is the client's, where forwards is not.** Going on is a claim the state machine settles;
going back is a claim only the client can make, so `BackAsync` is the one movement they drive and
`StepBack` names where it lands in the rail's own words rather than saying "back." It costs nothing to
allow: a step is re-entered exactly as it was the first time, a fresh agent and session reading the
configuration as settled fact. **The memory is the configuration, not the conversation** — the same
rule that crosses a pass boundary going forwards.

### The map first, then the agents

**Alembic edits a domain, not an agent** and a domain is `Intents` plus the `Agents` that answer them
one each — so the interview opens on `Intents`, the ground everything else is planted in, then walks
the list, five passes per entry, until every intent has its agent.

**A domain is a choice, not an inventory.** The map is the subset of the client's own processes they
are installing Morgana to run, not a description of their business — asking for their work
exhaustively turns the pass into a confession and half the resulting list is capability nobody asked
for. What they do hand over has to be complete, because an intent missing from the map is a process
the domain will never take and an intent nobody could tell from the one beside it is a user meeting
the wrong agent — both are cheapest to catch while the client is still choosing words.

**All four of an intent's fields are written there and that is the same argument twice.** A
description is read by the classifier *against every other description*; a label and its sentence are
read by a user *against every other button* — both correct only side by side, so both belong to the
pass with the whole set in view, written by Alembic, stated back, corrected, never asked of the
client one at a time. This is also why no later pass can write an intent: `SetIntent` does not exist,
and `AgentTarget` declares `SetAgentTarget` and nothing else that writes. The order is forced twice
over: descriptions are weighed *against each other*, so the map must be drawn and read back whole
(`GetDomainMap`) before it's settled; and `Instructions`/`Formatting` speak about the toolkit, so they
cannot be written before it exists.

| Pass | Runs | Settles | Cannot touch |
|---|---|---|---|
| `DomainMapper` | once | the whole `Intents` section: every name, description, label and opening sentence | everything about every agent |
| `AgentTarget` | per entry | the agent's `Target` and the `ConsultMeFor` it writes from it | the intents, its voice, tools, `Instructions`, `Formatting` |
| `AgentPersonality` | per entry | the agent's `Personality` | everything else, the `Target` included |
| `AgentToolkit` | per entry | the tools, their descriptions, their parameters, scopes and sharing | what the agent *is*, `Instructions`, `Formatting` |
| `AgentInstructions` | per entry | `Instructions` | everything already settled, `Formatting` included |
| `AgentFormatting` | per entry | `Formatting` | everything else, all of it settled |
| `DomainColleagues` | once, at the end | which agents may consult which and the boundary sentence each edge contradicts | every intent and every section of every agent but that one sentence |

**`ConsultMeFor` is written by the `AgentTarget` pass and never asked for.** It is the same scope the
`Target` settles, addressed to a different reader — a colleague deciding whether a question belongs to
this desk — so asking the client would be asking them to answer twice. The pass writes it itself the
way `AgentPersonality` writes prose from traits and `MissingTarget` holds the pass open until both
stand: an agent finished for itself and mute to every colleague is not finished. It states a
territory, never a list of what the agent can do, because a caller handed an inventory of functions
rules its question out instead of asking it; and it never carries a rule about consulting, which the
framework's own `PeerConsultation` policy already binds. Every agent gets one, edges or none — it is
published on the agent's A2A card and read only by others, so it costs its author nothing.

**`Target` and `Personality` are two passes, not two questions of one.** A `Target` is *dictated* —
the client knows the work and can say it — while a voice is *recognised*: nobody has a ready sentence
about how their own staff should sound, so it gets its own screen and its own form: `SetTraits` puts
eight to fourteen model-picked adjectives under the question, several at a time or none, text box
still open — deliberately not `SetChoice`, since one word isn't an answer until joined by others. They
must sit inside Morgana's own voice; what comes back is prose the pass writes itself, naming which
facet of her this agent is, read back composed so a voice arguing with hers is visible.

The `AgentFormatting` pass is the one place the loop stops for the client: letting a finished agent
into the domain is the single decision of the interview that is theirs and `AcceptAsync` opens the
next `AgentTarget` pass automatically if the map has another entry.

### The colleagues, last

`[ConsultsAgent]` is the one thing about an agent that **cannot be settled while the agent is being
written**: it is a relation and half its ends do not exist yet. So it is asked once, at the end,
over the whole domain — the agents of earlier sittings and uploads included, not only today's map —
which makes `DomainColleagues` the mirror image of `DomainMapper`: the map opens the domain by
settling every intent against every other and this closes it by settling every agent against every
other. Both sit outside the lap for the same reason and neither settles an agent.

`AcceptAsync` opens it when the map is spent, the domain holds more than one agent and this is not
an edit — a revision is one agent and where it ends is the walk it was opened from, not a
domain-wide question about all of them. It is also the one pass whose **own agreement ends the
interview**: nothing is waiting to be let into the domain the way a finished agent is, so there is no
acceptance gesture and `AnswerAsync` commits and abandons where the others hand over to the client.

**An edge and the prose it contradicts land together, or the edge is a defect.** This is the failure
the shipped `Examples` domain demonstrated before it was fixed: `BillingAgent` carried
`[ConsultsAgent("inventory")]` while its own `Instructions` said orders *belong to another bench —
say so plainly, never answer from the invoice*. The model reads a function offering the colleague and
a flat imperative refusing the subject. Morgana's `PeerConsultation` policy now decides that
collision by precedence — it is `Critical` and the domain layer is subordinate — but an agent whose
own prose has to be overruled on every turn is still a defect: the contradiction is paid in tokens
and settled by a model rather than by its author. So `DeclareConsultation` takes the asking agent's rewritten
`Instructions` **in the same call** — and optionally the colleague's, only where its own words would
have it refuse what it is now asked — and `CommitColleagues` writes attribute and prose in one go.
What that prose must never carry is a rule about *when* to consult, how briefly, what to expect back
or what to do with the answer — nor that the answer is given in the agent's own voice, nor that the
customer is not sent away. Every one of those is a critical rule of the framework, binding above
anything the domain layer says and a second copy below is a second voice claiming the same
authority. Nor may it carry the **colleague's** territory. The moment an agent declares
`[ConsultsAgent]`, `MorganaAgentAdapter` appends the colleague's own `ConsultMeFor` to the asking
agent's prompt (`ComposeColleaguesDeclarationAsync`, one line per colleague: *function name →
that colleague's statement of its scope*), beside the `PeerConsultation` policy that carries the how.
So the asking agent already knows the colleague exists and what falls to it, in the colleague's own
current words — restating that in the asking agent's `Instructions` is the same contradiction from
the other side, stale the day the colleague revises its own scope. What the rewritten sentence states
is fact about this agent's **own** books: what is on them and — where a limit is still worth stating
— what plainly is not, with nothing about where that goes instead. The defect the pass repairs is
prose that *fights* that framework layer: a flat refusal of the subject, or a hand-off naming which
other counter to try.

What the client is asked is a question about **their own work** — whether the accounts desk really
rings the greenhouse when a customer asks what a charge bought — never which agent should call which,
which is machinery they were never shown. Alembic finds the candidates itself, by reading the domain
for the shape that gives an edge away: an agent's own prose sending a customer to another bench for
something that bench's tools can answer.

An edge is touched in three places and each does the one thing the others cannot:
- **The interview asks.** Whether two desks ring each other is a fact about the business, not about
  a deployment — unlike the tier and the MCP servers it sits beside in `AgentCodeFacts` — so it is
  asked in the only place having that conversation and it is the only place that can rewrite the
  boundary sentence in the same gesture.
- **The emit page holds it**, as one more C# fact beside the tier and the MCP servers, for the
  upload path described below. It writes the attribute and never a word of prose. It is also the
  **only** place a colleague published by *another Morgana*, declared under that
  deployment's `Morgana:AgentToAgent:OutboundSystems` — can be declared at all and that is not a gap in
  the interview but the same criterion applied one step further: which Morgana answers is not a fact
  about the client's business and the emit page is the one point of the workbench where the hand on
  the keyboard is a developer's, finalizing a domain already distilled. The two are told apart on
  sight there, because they can be trusted differently: the tick list is drawn from the domain and
  cannot be wrong, while a remote colleague is two free fields — an intent and an instance — neither
  of which anything can check, here or at startup. That instance's own agent card is what knows, read
  on the first consultation, so a mistyped intent is a run-time warning and a colleague quietly
  missing rather than a startup failure; a mistyped *instance name* is startup-fatal, since that name must
  match a configuration entry. The migration report says so on every report, naming the instance to
  declare and stating that neither its address nor its key travels in the archive.
- **The coherence pass reports.** It cannot declare an edge — that is structural and
  `[ConsultsAgent]` naming an unhandled intent is startup-fatal — but it is the only one of the
  three that reads the declaration against the prose, wherever either came from and says they
  disagree.

And one thing it is deliberately not:
- **Not the only way in, because an upload has no C# at all.** `agents.json` carries the attribute
  no more than it carries a namespace, a tier or an MCP server — and those three are settled on the
  emit page, which is where a client who uploaded a domain and only *walked* it meets its C# facts.
  So the edge is settleable there too, as one more tick beside them. What that page cannot do is the
  half this step exists for: it never touches the client's own prose, so a boundary left refusing
  what the new colleague answers stays exactly as it was and what reports it is the coherence pass's
  `colleague-out-of-step`. Written domain: the interview does both at once. Uploaded domain: tick,
  then ask the pass. A remote colleague takes that second path whichever way the domain arrived,
  for the reason above — and the boundary it contradicts is repaired the same way, by the pass that
  reads the two against each other.

The mode row still splices in and it says *YOU ARE COMPOSING* — false of a step that only rewrites
one sentence of finished prose. Rather than a conditional per pass, which is the arrangement
`ModePromptTests` exists to prevent coming back, the pass's own `Target` states the fact flatly in
its first line and it is read last, where the most specific layer belongs.

**The fallback intent is not on the map and never was.** `other` is where the classifier sends what it
cannot place; `DomainDraft.EnsureFallbackIntent` puts it in every domain (greenfield at commit,
uploaded at import) and `DeclareIntent` refuses the name — the one element of a domain no interview
authors and no client edits.

### The walk: a domain is edited, not only added to

An agent already in the domain can be opened and corrected and most of what that needed already
existed: a step re-entered with a fresh agent/session reading settled fact (`BackAsync`); an entry out
of the domain while in hand, so it cannot be committed twice; removing a tool or parameter
(`DropTool`/`DropToolParameter`). What was missing was a way in **from outside the interview**.

**Correcting is a fourth intention, so it is a fourth door.** Somebody who uploads a domain to fix a
tool or a phrase is not adding to it, weighing it or packaging it and it does not ask *which* agent —
whoever comes through it usually cannot say where the problem is, they recognise it when they read
it — so it opens on the first agent and leafs; the client who *does* know finds it the same way, with
the walk's own prev/next. Import's own agent cards, once a shortcut straight to one of them, are
read-only now: one way in is the point and a second door reaching the same screen is a second thing
to explain where there is one.

**Leafing costs nothing, because leafing is reading.** `Pages/Revise.razor` calls no model and writes
nothing to the Draft — an agent read on the way past is never taken out of the configuration, so
closing the tab mid-walk loses nothing and the screen arrives instantly rather than after a
Performance call for a step nobody stopped at. **The agent is shown whole, for reading**: five rows
under the rail's own names, never clipped, but none is its own way in any more — one door per agent,
always opening at the Target. The map is not one of the five and cannot be: it settles every intent
*against every other one*, so reopening it over a single agent would be the one pass with nothing to
compare its work to.

What `AgentRevision` carries is what the domain would otherwise lose while an agent is out of it: its
**place in the two lists** (so a fix never walks an entry to the bottom), the **provenance it arrived
with** and **what it read when it left** (opened and left alone goes back `Imported`, since a
migration report listing everything merely *opened* is a report nobody reads). The **C# facts are not
rewritten** on an edit — only a fresh agent gets proposed class names flagged `Inferred` — except the
one fact an edit can genuinely create: native tools where there were none now need a class to be
emitted into.

**An edit always opens at the Target and chains forward exactly like composing.** The earlier design
had a correction settle the one section it was opened on and stop, on the grounds that carrying the
client through the four sections after it cost them four steps nobody asked for — but that traded one
hazard for a worse one. Composing cannot leave a section wrong, since the toolkit is settled after the
Target and against it; editing used to be able to, because the pass that noticed a `Target` now
promising what no tool backs, or `Instructions` routing through a tool that's gone, had no tool to fix
a section that wasn't its own and could only name the gap in the client's words and stop. It no
longer needs to: `ReviseAsync` always opens the Target, `AnswerAsync`'s loop no longer tells composing
and correcting apart and an edit walks `Target → Personality → Toolkit → Instructions → Formatting`
exactly as writing an agent for the first time does — so the section left wrong is the very next pass
in the chain, using the same tools that pass would use to write it from nothing. `Revise.razor`'s five
rows are read-only, for exactly the leafing they were always for; the one thing they no longer do is
choose where the interview opens.

The `Correcting` row licenses the fast path this needs: *check this section against what just
changed, not only against what the client asked about here by name* and where nothing changed
upstream that touches this section, settle it with the offered choice **without calling the section's
own `Set` tool at all**, so it survives untouched rather than rewritten in different words — what
keeps a one-line `Target` fix from costing five real interrogations instead of one question and four
quick confirms. The coherence pass still catches a `Target` with nothing behind it, but now mainly in
domains this interview hasn't touched (an imported agent left alone, one hand-written outside
Alembic) — its own job stays relations *between* agents, which no amount of walking one agent's own
sections could ever see.

Both are under test. `AddedCapabilityFixture` adds a coupon capability to `Inventory`'s `Target` — the
hardest of the four agents, since its eight tools make the toolkit *look* comprehensive — and drives
the edit through to `AgentFormatting`; `CrossSectionEditTests` asserts, deterministically, that the
toolkit grew to cover it and that a tool was actually declared rather than merely mentioned.
`ExamplesDomainFixture` covers editing from the other end, starting from the shipped
`Examples/agents.json` rather than interviewing a domain into existence: its first run found a real
defect, `AcceptAsync` assigning `Agent.ID = Intent.Name` unconditionally, silently recasing an agent's
own ID whenever it and its intent differed only in case. `ModePromptTests` holds the prompt
architecture itself, without a model: each pass gets one mode row, the right one and the two composed
prompts differ by that row and nothing else.

**A pass is never left to work out which job it is doing.** The message that opens a step states it
as a fact — *nothing of this agent is written yet*, or *this agent already exists and you are
correcting it*, every section quoted back — read off the agent, never off which pass is running: a
per-pass sentence could only be right for one of several ways a step is entered (straight down the
interview, stepping back, resuming a saved sitting, correcting from the walk) and a table that tried
once sent the client the mapping question three steps behind where they stood.

Each pass is a **fresh agent and a fresh session** by design, not limitation — a pass carrying the
whole interview in its context spends it re-litigating decisions already taken. What crosses a pass
boundary is the *configuration*, via `GetAgentSoFar`/`GetToolkit`, read as settled fact; the client's
own transcript is continuous and the restart belongs to the model alone. The right-hand column of
the pass table above is enforced structurally, by each pass's own `Tools` declaration in
`alembic.json` and `InterviewState.Missing()` is pass-scoped the same way — a toolkit pass owns no
fields at all, since an agent with no native tools is the legal MCP-only shape.

### One question, one step

The characteristic failure of an LLM-conducted interview is not a wrong answer, it is **circling**:
the model asks the same thing again, each phrasing slightly better, until the client stops answering.
The fix is doctrine, high in Alembic's own `Instructions`, rather than a patch per pass: every
question is a step and an answer advances it if **anything can be written down** — the rest is
inferred, proposed and corrected rather than asked again. Asking twice tells the client their answer
wasn't good enough; asking three times ends the interview whether they say so or not.

The toolkit pass states the one instance the doctrine can't know on its own: **scope is inferred,
never asked per parameter.** The client is asked once, about their setup — what the system already
knows about a user the moment they arrive — and everything on that answer is `context` for the whole
toolkit, everything else `request`. `Shared` is inferred from what the value *is*: an identity the
domain establishes once is shared, an agent's own working value is not.

### Validation runs before the recap

The order is the design: composing a beautiful prompt for a domain that would not start is a way of
lying to the client with something that looks like evidence.

Every check in `DraftValidationService` is decidable by reading the Draft, no model asked — most
restate a rule the framework already enforces at startup and the duplication is the entire value: an
`InvalidOperationException` from `HandlesIntentAgentRegistryService` arrives after the client has
packaged, deployed and run; the same sentence here arrives while it costs nothing to change. Each
finding carries a `Because` naming the rule.

What it cannot see matters as much: whether two intent descriptions collide in the classifier, or an
agent's `Instructions` contradict its `Formatting`, needs a model — that is the cross-agent coherence
pass. Two checks that once lived here were removed rather than kept as "true but expected": whether an
agent's C# facts are still Alembic's guess and whether an agent declares no native tools — both true
of *every* freshly imported or authored agent without exception, so neither ever discriminated a
domain needing attention from one that didn't.

### The starter scenarios: templates in, one domain out

A domain agent *is* its prose, prose gets edited and the only way to know an edit broke nothing is
PromptHarness. Alembic writes the **starting set and no more**: it knows what the agents were
designed to do, which is what a first scenario is made of and nothing about what will actually go
wrong, which is every scenario after it.

**Running them needs a source checkout of Morgana, not a deployed one** — PromptHarness boots Morgana
in-process to observe it, unlike the `.csproj` this archive ships. Observing a *remote* Morgana
instead is a real redesign belonging to PromptHarness, not Alembic; until then the YAML travels
regardless, for the client who does have Morgana's source beside it.

The split that makes this work: **which behaviours are worth protecting** is knowledge about agents,
true before any client arrives, settled once as `Distiller/Harness/Templates/*.yaml` — one file per
behavioural use-case, placeholders where domain words go. **Which words say them here** is knowledge
about the client's business, which only a model that has just read the whole domain can supply, so
the model derives: replace every `{{…}}`, change nothing else. Asking a model for "two or three
scenarios" was the earlier, wrong shape — it made the model choose which behaviours matter, the
decision it's worst placed to take and the answer was the same three shapes every time.

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

Applicability is decided in C# only for the right-hand column. Everything semantic is the model's: a
template with no instance comes back `not-applicable:` with a reason and is dropped, since a
read-only toolkit has no confirmation to protect and a scenario demanding one would fail a correct
agent every run.

**Nothing here is copied from `PromptHarness/`**, which is entirely infrastructural — the templates
carry its shape because they were written against it, not because they are pieces of it. That is what
makes the suite 100% domain **structurally**: the vocabulary a derivation is allowed is exactly the
union of keys the templates use — fourteen, all domain — so framework-only keys are not reachable at
all.

**A derivation may drop a key and may never add one** — the whole check, enough because the template
*is* the vocabulary. It runs at the emit because it can't be caught later: `ScenarioLoader` is built
`.IgnoreUnmatchedProperties()`, so a key the harness doesn't recognise is dropped without a sound —
the scenario loads, runs, passes and asserts nothing. Every derivation is held to its template's key
set, plus two other silent failures: an unresolved placeholder, a document with no turns. A scenario
that fails still ships, the problem written across its top — a silently missing scenario costs the
client more than a visibly broken one.

**The discretionary pass** runs once more after the base, handed the domain and the base **in full**
so an addition can neither repeat what's already asserted nor contradict it and may add up to two
scenarios of its own. **None is the ordinary answer**: a domain the base already describes is
well-designed, not a gap.

### The coherence pass

The other half of reviewing a domain and the half `DraftValidationService` cannot do. Whether two
intent descriptions overlap enough to collide, or whether one agent's `Instructions` contradict
another's `Formatting`, is about meaning — the most expensive defect a multi-agent domain carries,
since no downstream prose resolves it and the user meets it as an agent answering the wrong question.

Every defect it looks for is **relational**, which is why the interview cannot close them all — it
settles the map together (catching classifier collisions while words are still being chosen) but
writes each agent's prose alone: overlapping intents; two agents claiming the same capability; a value
one publishes as `userId` another expects as `customerCode`; ground the domain implies and no agent
covers; an agent promising what no tool backs; two toolkits reaching the same system under two
different shapes; an agent whose declared colleagues and own prose contradict each other. It's handed
the domain's exact words, never a summary — a summary is precisely the
step that would smooth an overlap away before the model saw it. The last defect carries its own
exception, or it fires on every domain: two agents needing the same lookup through their own tools is
ordinary; the same work under two different shapes is the defect, since each shape is a separate
integration to keep true.

**The classes are data, not prose and the client picks them.** Each one is an entry in the
`DomainValidator` prompt's own `Aspects` declaration — `Id` (the `kind` it answers with), `Label` and
`Summary` for the checkbox, `Description` for the block the model reads — so the boxes on Review, the
prose the pass is composed from and the `kind` values it may return are one list read three ways,
never three lists to keep in step. `CoherenceService` splices the selected blocks into `((aspects))`
and their ids into `((kinds))`; **an unselected class is not sent at all**, rather than sent with a
line withdrawing it, because a rule a prompt states and then takes back is read as a rule with an
exception and the exception is what a model gets wrong.

**Nothing is ticked on arrival and the pass will not run until something is.** Running it whole was
the earlier shape and it was worse in the way that matters: a client comes to this page with one
question about their domain and a report answering six is a table to sift, with the class they came
about ranked beneath five they did not ask for. The button counts their own ticks back to them
(*Ask about these two things*) and it comes back after a clean result only when the ticks now differ
from the ones the last run carried — the same question over an untouched domain would spend a
Performance call to hear the same answer, a different question would not.

**`colleague-out-of-step` is the one class that reads something outside the four sections.** An
agent's colleagues live in its C#, so `Describe` states them per agent — either the list, or that it
declares none — and the class catches the disagreement in both directions: a boundary sentence still
sending the customer elsewhere for what a declared colleague answers (the expensive one: the function
is offered, the flat instruction wins and the consultation is paid for in prompt tokens and never
happens), or prose promising to ask a colleague that is not declared. Its fix is **always prose**,
and `CoherenceApplier` is told so: gaining or losing a colleague changes the client's C# and is
theirs to do, on the interview's closing step or on the emit page. The fix is also told what it is
**not** — a signpost. The framework already appends the colleague's own `ConsultMeFor` to the asking
agent's prompt (see *An edge and the prose it contradicts land together*), so the fix never writes
"you can ask X about Y" into the agent's `Instructions`; it strikes the refusal or the hand-off that
fights what the framework supplied and, where a boundary is still wanted, leaves only a fact about
the agent's own books — never the colleague's territory, name or desk.

It answers JSON, the one place in Alembic that does, since this output is sorted and tabulated rather
than read as prose. And it **advises, never blocks**: a domain expert who disagrees with it about
their own business is usually right.

## Project Structure

```
Alembic/                              # Container: Distiller/ (the workbench) + PromptHarness/ (its own harness)
  Directory.Build.props               # Build settings, version, package metadata — shared by both projects
  Directory.Build.targets             # Regenerates the root .env.versions on each build
  Alembic.Dockerfile                  # Multi-stage container build (root context)
  Distiller/
    Distiller.slnx                    # Solution (sibling of Morgana.slnx, Cauldron.slnx, …)
    Distiller.csproj                  # .NET 10 Web SDK, ProjectReference → Morgana.AI; AssemblyName Alembic
    appsettings.json                  # Morgana:LLM section (Performance tier only)
    alembic.json                      # Alembic's OWN prose AND tool declarations, embedded resource
    Properties/launchSettings.json    # Dev profile: https://localhost:5005
    Program.cs                        # DI wiring and app pipeline
    App.razor                         # Blazor root component
    _Imports.razor                    # Shared @using directives
    Model/
      DomainDraft.cs                  # The Draft: DomainDraft, IntentDraft, AgentDraft, ToolDraft, …
      AgentsConfigurationFile.cs      # The on-disk shape of an agents.json (Morgana's own Records inside)
      DraftProjection.cs              # Draft → Records, shared by the exporter and the recap
      ValidationFinding.cs            # Severity, Where, Message, Because
      AgentRecap.cs                   # System prompt and tools, the two rungs shown apart
      AgentRows.cs                    # The written-so-far rows, computed once for the map tag and the panel
      InterviewState.cs               # The interview's C# state machine: the map, where it stands on it, the pass
      Provenance.cs                   # Imported / Revised / Authored
    Interfaces/                       # One per Services/ default below, same name minus "I"
    Harness/                          # Alembic's own harness component — owes PromptHarness nothing
      Templates/*.yaml                # One behavioural use-case each, domain words left as placeholders
      ScenarioTemplate.cs             # A template: its `#@` brief and the scenario shape below it
      ScenarioTemplateLibrary.cs      # The templates, embedded; which apply; the union vocabulary
      ScenarioDerivation.cs           # A derivation checked against the vocabulary it was allowed
    Services/                         # Default implementations
      AgentlessConfigurationService.cs #  IAgentConfigurationService, empty by construction
      DraftImportService.cs           #  Uploaded agents.json → Draft
      DraftExportService.cs           #  Draft → agents.json (the round-trip invariant)
      DraftSerializationService.cs    #  Draft ⇄ alembic-draft.json (the interview's save file)
      DraftValidationService.cs       #  Everything decidable about a Draft without a model
      DraftStateService.cs            #  The Draft under construction, one circuit, one fallback snapshot
      RecapService.cs                 #  Draft → the prompt the model really reads
      AlembicPromptService.cs         #  Alembic's own prose + tool declarations, from alembic.json
      InterviewService.cs             #  Conducts the passes, folds each finished agent into the Draft
      InterviewTools.cs               #  The tools Alembic calls while conducting a pass
      SolutionEmitService.cs          #  The .csproj/.slnx the generated sources live in
      CodeEmitService.cs              #  The deterministic half of the generated C#, X.g.cs
      ToolMockService.cs              #  The authored half, X.cs — a working mock per toolkit
      StreamedCompletion.cs           #  One completion, streamed and resumed until the model stops on its own
      MigrationReportService.cs       #  What this domain changes against the one uploaded
      ScenarioAuthorService.cs        #  Derives the starter PromptHarness suite from Harness/Templates
      CoherenceService.cs             #  The relational defects no per-agent pass can see; advisory
      CoherenceApplyService.cs        #  Carries out one accepted coherence finding
      CoherenceApplyTools.cs          #  The tools the apply pass calls, narrower than InterviewTools
      AssetPackageService.cs          #  The one archive: config, sources, mocks, reports, save file
    Pages/_Host.cshtml                # Blazor Server host page (Server: prerendering mounts twice)
    Pages/Index.razor                 # Landing: the alembic and the two ways in — distil a new domain, or continue one
    Pages/Import.razor                # Upload an agents.json, a save file or an archive; four doors, agents read-only
    Pages/Revise.razor                # The walk: the domain's agents read one screen at a time, one door in, always at Target
    Pages/Review.razor                # Findings, then the composed prompts
    Pages/Interview.razor             # The wizard: drives the state machine, owns none of the pieces
    Pages/Morganize.razor             # The turnkey end: validate, then emit the one archive
    Shared/MainLayout.razor           # Layout wrapper; carries the alembic as a mark on every page but the landing
    Shared/Back.razor                 # The way back and it is the way you came
    Shared/FinalizationRail.razor     # The two-station rail for Domain Finalization: validate, then leave
    Shared/Interview/                 # The wizard's pieces, one component each
      Journey.razor                   #   the rail and its lap, the entries under them, what this step asks
      Question.razor                  #   the question, the choices, the box
      StepBack.razor                  #   the way back, named after where it lands, beside the way on
      AgentSoFar.razor                #   the agent's own rows, folded behind its name
      SaveWork.razor                  #   the work so far, in one file, taken away in one click
      AgentWritten.razor              #   the one decision that is the client's and the join to the next entry
    wwwroot/css/                      # Eight files, cascade order set in _Host.cshtml
      palette.css                     #   the palette alone: every file below spends it, none redefines it
      base.css                        #   reset, page shells, the mark
      landing.css / import.css        #   the two entrances
      revise.css                      #   the walk: reading surface with exits, no question on it
      panels.css / controls.css       #   what is read in a panel; buttons and shared furniture
      interview.css                   #   the interview, whose three states have to agree in one place
    wwwroot/favicon.svg               # The vessel at 16px: belly, neck, spout, one spark
  PromptHarness/                      # Non-regression harness for Alembic itself — own solution, not Distiller's
    PromptHarness.slnx                # Solution of its own, the same way the repo-root PromptHarness/ has one
    PromptHarness.csproj              # xunit v3, ProjectReference → ..\Distiller\Distiller.csproj
    AssemblyInfo.cs                   # Assembly-wide fixture + serial test collection
    Infrastructure/                   # AlembicHostFixture, InterviewDriver, ArchiveCompiler, Judge
    Fixtures/                         # Bistro Luna, interviewed into being; the Examples domain, imported and corrected
    Tests/                            # DoctrineTests, MappingTests, InterviewConductionTests, FinalizationTests,
                                      #   EditingTests, CrossSectionEditTests, ModePromptTests
```

## The Draft

The single artifact the interview fills, the validator checks, the recap composes and the emit
reads. Three things about it are decisions rather than mechanics:

**Why not the `Records` types directly.** They are the *serialization* model: immutable, complete,
positional. The Draft is the *editing* model and an interview in progress is incomplete by
definition — a tool whose description has not been asked for is a different state from one whose
description is deliberately empty and only a nullable field distinguishes them. Every nullable
string in `DomainDraft.cs` means "not asked yet."

**The fifth section.** `AgentDraft.ConsultMeFor` is modelled like the other four and travels the
round trip with them — `DraftImportService` reads it off an uploaded prompt and `DraftProjection`
writes it back — which matters more here than for a section Alembic authors freely: it is a top-level
member of `Records.Prompt`, not an `AdditionalProperties` key, so `UnmodelledProperties` would not
have caught it and a client's uploaded statement would have been silently dropped on export.

**What survives that Alembic does not understand.** AdditionalProperties keys other than `Tools`
are kept verbatim in `AgentDraft.UnmodelledProperties` and written back untouched — the round-trip
invariant must not depend on Alembic having a use for every key it meets. The `Tools` key is matched
**ordinally**, deliberately: `Records.Prompt.GetAdditionalProperty` looks it up in a plain
`Dictionary<string, object>`, so a differently-cased key is invisible to the framework and must stay
invisible here.

**Provenance** (`Imported` / `Revised` / `Authored`) exists so Alembic rewrites only what it owns and
can *report* honestly. It is not what preserves untouched content — that is the round-trip invariant,
which holds regardless.

## The round-trip invariant

**A configuration that goes in comes back out equivalent.** A client uploading a domain of ten agents
to add an eleventh gets the other ten back untouched and Alembic does not need to understand them to
promise it.

Equivalent, not byte-identical and the difference is what the format actually means:
`AdditionalProperties` is a *list* Morgana looks keys up **across**, so the grouping carries no
information; defaults are written explicitly; and emoji come back as escaped surrogate pairs, since
`UnsafeRelaxedJsonEscaping`'s allow-list stops at U+FFFF. Verified against `Examples/agents.json`:
exported JSON is semantically equal field by field, re-importing yields an identical Draft and
exporting that Draft again is byte-for-byte stable — a fixed point, so a file that has been through
Alembic once stops moving.

`AgentCodeFacts` holds what `agents.json` cannot: namespace, class names, tier, MCP servers and the
colleagues an agent may consult — each a `Records.PeerReference`, the framework's own type, carrying
an intent and the instance publishing it where that is not this domain (the same reuse as
`Records.LLMTier` beside it: a second vocabulary for one thing is what makes two projects drift). On import
all of it is unknown, so Alembic proposes class names from the framework's naming convention and
flags the record `Inferred`; namespace and tier are left null rather than guessed, since a confident
wrong value is worse than an empty one the interview will ask about. The inference is a genuine guess
and meant to be seen as one: against `Examples` it proposes `MonkeysAgent` where the real class is
`MonkeyAgent`.

## DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `ILogger` | Singleton | Several Morgana.AI services take a bare `ILogger`, not `ILogger<T>` |
| `IAgentConfigurationService` | Singleton | `AgentlessConfigurationService` — both sources empty and empty **by construction**: the domain Alembic works on is the **uploaded** one, never one compiled into this process. The framework's `EmbeddedAgentConfigurationService` would reach the same state by reflecting over every loaded assembly and then warning that no `agents.json` was found — a true sentence about a Morgana deployment that has lost its domain and a misleading one here. Declaring the absence costs a scan less and reads as the design it is |
| `IPromptResolverService` | Singleton | `ConfigurationPromptResolverService` — resolves the framework prompts from `morgana.json`, embedded in Morgana.AI and free through the project reference |
| `IPromptComposerService` | Singleton | `ConfigurationPromptComposerService` — assembles what the model reads; this is what makes the recap a real composed prompt |
| `ILLMService` | Singleton (factory) | Provider selected by `Morgana:LLM:Provider`. **Never resolved during startup**, so a working copy without credentials still builds, boots and serves the shell — the failure surfaces on the first call, with the provider's own message |
| `IDraftImportService` | Singleton | `DraftImportService` — uploaded `agents.json` → Draft. Holds no per-client state; it only projects one shape onto another |
| `IDraftExportService` | Singleton | `DraftExportService` — Draft → `agents.json`. Rebuilds Morgana's own record types and serializes those, so the file Alembic emits and the file Morgana reads are the same type seen from two sides |
| `IDraftSerializationService` | Singleton | `DraftSerializationService` — Draft ⇄ `alembic-draft.json`. An interview over a real domain does not fit in one sitting and Alembic has no database |
| `IDraftValidationService` | Singleton | `DraftValidationService` — the deterministic checks, each restating a rule the framework enforces later and more expensively |
| `IRecapService` | Singleton | `RecapService` — drives `IPromptComposerService` over the Draft. Deliberately almost empty: anything Alembic added on top would be a claim about the prompt rather than the prompt |
| `IAlembicPromptService` | Singleton | `AlembicPromptService` — loads `alembic.json` from this assembly, the way `ConfigurationPromptResolverService` loads `morgana.json`. Unlike it, refuses to degrade to an empty set: an interviewer with no prose is not diminished, it is silent |
| `IInterviewService` | **Scoped** | `InterviewService` — one interview per circuit. Builds a Microsoft.Agents.AI agent via `MorganaToolAdapter` + `AsAIAgent`, on `GetChatClient(Performance)` directly rather than `CompleteWithSystemPromptAsync`, which always takes the cheapest tier |
| `IDraftStateService` | **Scoped** | `DraftStateService` — the Draft under construction, in the circuit and nowhere else; a timer keeps one snapshot as a fallback for `Save my work`, never as a resumption path — see *Two files, one gesture* |
| `ISolutionEmitService` | Singleton | `SolutionEmitService` — the `.csproj`/`.slnx` the generated sources live in, string-templated with the same discipline as `ICodeEmitService` |
| `ICodeEmitService` | Singleton | `CodeEmitService` — the deterministic half of the generated C#: same Draft, same bytes, so a re-run diffs to exactly the change |
| `IToolMockService` | Singleton | `ToolMockService` — the half a template writes badly: a working mock per toolkit, streamed and resumed until the model stops of its own accord |
| `IMigrationReportService` | Singleton | `MigrationReportService` — what this domain changes against the one uploaded, produced unconditionally, greenfield included |
| `IScenarioAuthorService` | Singleton | `ScenarioAuthorService` — derives the starter PromptHarness suite from `Harness/Templates/*.yaml` against the domain just authored |
| `ICoherenceService` | Singleton | `CoherenceService` — the relational defects no per-agent pass can see; advises, never blocks |
| `ICoherenceApplyService` | Singleton | `CoherenceApplyService` — carries out one accepted coherence finding via `CoherenceApplyTools`, reached only on an explicit Apply |
| `IAssetPackageService` | Singleton | `AssetPackageService` — the one archive: agents.json, generated sources, mocks, `MIGRATION.md`, `README.md`, the save file |

Each registration in `Program.cs` carries its own reasoning in a comment beside it; this table is an
index into those, not a second copy of them.

## Why the project reference is Morgana.AI, not Morgana.Contracts

Cauldron, Grimoire and Rune reference `Morgana.Contracts` because they exchange wire DTOs. Alembic
exchanges none. What it needs is the **domain model of a Morgana configuration** — `Records.Prompt`,
`Records.ToolDefinition`, `Records.ToolParameter`, `Records.Intent` — plus `IPromptComposerService`.
Parsing an uploaded `agents.json` is therefore free: there is no parallel representation to maintain.
`Morgana.Contracts` arrives transitively.

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Alembic:Work:AutosaveSeconds` | How often `DraftStateService` refreshes its one fallback snapshot for `Save my work`. It is the size of the window a dead connection can cost and the only reason it is not smaller is that each snapshot serializes the whole Draft. There is no retention setting beyond it: the snapshot dies with the circuit and the file behind `Save my work` is the only thing meant to outlive it |
| `Morgana:LLM:Provider` | `Anthropic`, `AzureOpenAI`, `Ollama`, `OpenAI` |
| `Morgana:LLM:{Provider}:Tiers:Performance` | `Options` (`ModelId`, `MaxOutputTokens`) + `MagicDust`. Only `Performance` is declared — Alembic never uses the Efficiency die. `MagicDust` carries **both axes at zero and nothing else**: metering off, which is the truth here — dust accounting is applied by `MorganaAgentAdapter` and Alembic goes to `GetChatClient(Performance)` directly, so the pricing is never read at all. It cannot be shortened to `{}`: the JSON configuration provider reads an empty object as `null`, `Records.TierDefinition` takes `MagicDust` as a non-nullable constructor parameter and the dictionary binder drops an element it cannot construct **without raising anything** — so the whole tier disappears and the failure surfaces, one page later, as `No tiers configured` |

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

## Conventions

- Behavioral concerns behind interfaces, default implementation alongside, DI registration in
  `Program.cs` — same pattern as the framework
- No parallel representation of a Morgana configuration: the `Records` types are the model
- Generated C# obeys the `X.g.cs` / `X.cs` split described above
- Alembic never writes to, reads from, or assumes anything about the client's filesystem