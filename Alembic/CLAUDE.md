# Alembic — Morgana's Authoring Workbench

## Where the rationale lives

This file holds what the code cannot tell you: decisions, the alternatives they were taken against
and the doctrine the prose obeys. **Every registration in `Program.cs` carries its reasoning beside
it, every service states its contract in its own XML doc and every pass states its own rules in
`alembic.json`** — read those for anything mechanical.

## What is Alembic

A **Blazor Server** application giving a client the *initial morganization* turnkey: an AI-conducted
functional interview distilled into a complete Morgana domain — intents, agent prose, tool contracts,
C# assets and starter non-regression scenarios.

The name follows the repo's habit of naming the instrument (Cauldron the vessel, Grimoire the book,
Rune the mark): an *alembic* is the apparatus that distils.

`Alembic/` holds two projects, each with its own solution: `Distiller/` (the workbench) and
`PromptHarness/` (its own non-regression harness, nested for tidiness and deliberately not a client
of Distiller's code).

## What Alembic is not

- **Not a channel.** It never calls a Morgana instance, holds no JWT, announces no `ChannelMetadata`,
  joins no pipeline. The only unit in the repo that is not a client of a running Morgana. Its sole
  external dependency is an LLM.
- **Not a filesystem tool.** At runtime it lives wherever the client deployed it, exactly like
  Cauldron, so it makes **no assumption of seeing the client's filesystem**: configuration arrives as
  an **upload** and leaves as a **download**.

The second point is load-bearing: Alembic cannot know which C# already exists client-side, so it
never guesses, patches or merges it. See *Regeneration contract*.

## Design decisions

### Performance tier, non-negotiable

Its whole job is writing **dispositive prose that does not contradict itself** — the exact task where
the `Efficiency` die amplifies contradiction-following failures. A wizard emitting a subtly
self-contradictory prompt is worse than no wizard, because the client has no instrument to notice.
Alembic runs once at onboarding, not per turn: the wrong place to save.

Consequence of the framework's no-cross-tier-fallback rule: **Alembic does not serve a single-tier
deployment** until a `Performance` entry is configured.

### Alembic is of Morgana and so is everything it writes

An agent of Morgana that produces agents of Morgana, composed the way one is — layered, fenced,
subordinate to her — from an `alembic.json` of **identical shape** to an agent's. Whoever tunes
Alembic does the job Alembic teaches.

Two layers, not three: **Morgana in her own words**, resolved live from `morgana.json` rather than
copied (her `Personality`, her `Target`, her `GlobalPolicies` **by name only**), then **Alembic's own
prose**. Her `Injections` are deliberately not read: splice templates, not subjects a domain author
could write a rule about. An earlier shared `Doctrine` layer had nothing to bind — Morgana's binds
global policies to turn machinery Alembic does not have.

**The second layer is stored deduplicated, read as one.** The passes differ only in which tools they
hold, so the identical half lives once and a pass carries only what is its own; the two are merged
**section by section under one set of labels**, so the model reads four sections with no seam.

**A third row says which of two jobs the step is doing: `Composing` or `Correcting`.** Written as
clauses inside shared prose, both jobs sat in every pass — which is how the model twice opened a
fully written agent as though blank.

What stays out of the top layer: the policies' bodies and her `Formatting`. Both govern a **channel
turn** Alembic does not have and handing a model rules about things that do not exist in its world is
the most direct way to manufacture the non-local contradictions this project exists to avoid.

### The sections and staying inside the universe

A sentence in the wrong section is worse than a sentence missing — the agent reads each for a
different purpose.

| Section | Answers | Size |
|---|---|---|
| `Target` | what this agent does well and existentially and what it is significant to say it does **not** do | 2-4 sentences |
| `Personality` | the empathy, language, tone and humanity it meets the user with — voice only | 2-3 sentences |
| `ConsultMeFor` | the competence this desk is characteristically the one to answer for | 1-2 sentences |
| `Instructions` | how it goes about it, what it is trying to achieve, what it must **not** do on the way | 2-5 sentences |
| `Formatting` | how it presents its **own** information: which shape suits which tool's output | brief, concrete |

An authored agent is **one agent of Morgana**, never a separate creature — never "a virtual
assistant". `Personality` **names which facet she is here** ("a formal and exacting witch"). Where the
domain admits it she gets something of her own to speak from (a ledger, a scroll); where it does not
— an ill pet wants no whimsy — she gets none. The colouring stays in **how she speaks, never in what
she claims to do**: a ledger may be gazed into, an invoice may not be conjured.

### Regeneration contract

| File | Owner | Rule |
|---|---|---|
| `X.g.cs` | Alembic | attributes, constructor, `partial` signatures — **always overwritten** |
| `X.cs` | the client | the working mock, then their real integration — **written once, never touched again** |

The split does double duty: non-destructive regeneration, *and* the line between what is templated
(deterministic, so a re-run produces no spurious diff) and what the LLM authors (a plausible mock,
which a template writes badly).

The seam for a tool class is `partial` **methods**, declared from the same `ToolDefinition` that goes
into `agents.json`. Two things come free: the pair `MorganaToolAdapter.AddTool` validates at startup
is correct by construction and a tool added to the configuration but forgotten in the code **does not
compile**. Every emitted parameter is a `string`, which is a statement about the configuration rather
than a shortcut: `Records.ToolParameter` carries no type, so the type lives in the C# and only there.

Because Alembic never sees the client's tree, the convention travels **inside the archive** and needs
no enforcing: a drifted signature is already a startup failure. Alembic's job is to surface it
earlier, through an unconditional **migration report** against the uploaded `agents.json`.

### The emit

The archive is **one** download because the pieces are only correct together: an `agents.json` whose
toolkit has moved on from the C# beside it is a startup failure and two downloads invite taking one.

The emit is **string templates and nothing else** — no Roslyn — so the same Draft emits the same
bytes. The mocks are the one artifact a template writes badly and they are **mocks and not stubs**
because of the turnkey promise: the client must be able to *talk to their agent on the first run*, the
only way to hear whether the prose is right. A `NotImplementedException` makes it unreviewable.

No output ceiling is declared in code — a source file's length is a property of the toolkit — so the
ceiling lives generously in `appsettings.json` and an empty answer throws rather than writing an empty
file that looks like success.

### The migration report

Unconditional, greenfield included: a report that only appears when something is wrong is a report
nobody has learned to read on the day it matters. It diffs the Draft against `DomainDraft.Baseline`,
the domain frozen at import and kept as a Draft so the comparison is like with like. `Provenance`
alone could not serve: it says an element was revised, never what it was.

### Two files, one gesture

Alembic has no database, so `alembic-draft.json` is the whole of its memory — the baseline and
`DomainDraft.Sitting`, the interview itself as it stood, included. **The sitting is what makes closing
the tab survivable.** What is saved is the configuration in hand, never the conversation: the map,
which entry, which step, what is written so far. Resuming re-enters that step the way `BackAsync`
does — a fresh agent and session reading what is there as settled fact.

`DraftStateService` also snapshots every `Alembic:Work:AutosaveSeconds`: a floor under one button,
**not** a resumption mechanism — a workbench that silently resumed something the client had not asked
to resume would be deciding for them what they came to do.

After a save the receipt says what is **actually in the bytes**, parsed back out of the downloaded
document rather than reported from the interview's own state: the worst bug this project has had was a
save handing back an empty file while the interview on screen was perfectly healthy. Only the file can
say what is in the file.

A save file and a configuration arrive through the **same upload control**, told apart **by reading
the file, never by its name**.

### The recap is the real prompt

The recap is **the composed prompt the model will actually read**, not a summary of the client's
answers — a summary would be Alembic grading its own homework. Shown as **two separate blocks, never
concatenated**, since each is read at a different moment of the turn: the two-layer system prompt,
then each tool description as the model weighs it.

The framework layer comes from the `morgana.json` embedded in the Morgana.AI this Alembic was built
against. Alembic deliberately does **not** accept an uploaded `morgana.json`: that would model a
capability the framework does not have.

### The card an agent carries

Morgana publishes one card per agent and builds it from three things: the intent's name, the
`ConsultMeFor` as the description a caller reads and the tools as skills. Alembic packs the same card
**during the interview**, so what the model weighs and what validation reports is what a caller will
be handed.

**The card evolves and never says two things at once.** From the moment the map names a desk it is
described by the phrase the classifier routes on; once the territory is settled that description
*becomes* the territory. The two are never shown side by side: a routing phrase says which utterances
land here, a territory says what this desk answers for and a caller weighing its question against both
is weighing it against two different things.

What is decidable by reading is checked without a model, in validation and in the harness: a territory
naming the desk's own tools is an inventory, one identical to the routing phrase is the classifier's
sentence recycled and two desks publishing the same territory are one desk as far as a caller can tell.

### Alembic is an agent and its tools are declared the way an agent's are

Assembled with the framework's own machinery, not an imitation: `MorganaToolAdapter` binds each tool
declaration in `alembic.json` to its delegate and `IChatClient.AsAIAgent` makes the agent. Not
`MorganaAgent` via `MorganaAgentAdapter`, which belongs to the routed world of `agents.json`,
`[HandlesIntent]`, base tools and per-conversation persistence Alembic has none of. **The reuse stops
exactly where the resemblance does.**

Tools rather than a structured reply: Alembic simply **talks** to the client and carries the
configuration out of band, so a malformed answer stops costing the client a turn. **A tool answers
back** — every method in `InterviewTools` returns a sentence *to the model*, so a `Target` arriving too
short is told so and corrects itself in the same turn. **What a pass may write is which tools exist**:
the constraint is the absence of a tool rather than a sentence asking for restraint.

Several tools stop Alembic writing blind — **it is the last reader of an agent before it exists**: the
intents already taken, the findings, the composed prompt, the toolkit read back whole and the card the
agent will present.

### Choices: a channel's contract, an interviewer's doctrine

`SetChoice` attaches a button carried by `Morgana.Contracts.QuickReply`, so the shape the client sees
is literally the shape their own agents will emit. The doctrine is nearly the inverse of Morgana's:

- **Never on a question about the client's domain** — a menu would replace their words with Alembic's.
- Only where the answer set is closed and known, which in practice is **the answer that adds nothing**.
- **The text box never closes.** A choice is an offer, never a gate.

**There is exactly one button and it carries the answer that adds nothing.** A second would carry the
opposite, which is never an answer: *something's missing* still has to be typed. The count is enforced
in the **signature**, so a second is not expressible.

### The interview: C# owns the state, the model owns the conducting

**What has been established, which pass is running and what may be written next are facts**, living in
`InterviewState` and `InterviewService`'s merge/readiness gate. **Which question to ask next is the
model's, as is how to turn an answer into dispositive prose.**

Readiness is **checked, not believed**: the model reports a pass settled, the state machine confirms
the fields are there and the next pass opens inside the same `AnswerAsync`. `IInterviewService` has no
`Advance` for this reason; it does have `BackAsync` and the asymmetry is the point — going on is a
claim the state machine settles, going back is a claim only the client can make.

The client never writes prose. They answer questions about their work; Alembic writes the configuration
and says what it understood — the asymmetry that spares a domain expert from becoming a prompt author.

**Every step says where it is before it asks anything**, in the **second person**, asking **what
happens, never what a word means**. **One section per question, never two** and every question after
the opening one is about their work in their words — never intent/tool/parameter/scope/context/domain/
configuration, each of which costs a beat of translation and comes back as the client's guess at
Alembic's vocabulary.

**A pass is never left to work out which job it is doing.** The message opening a step states it as a
fact — *nothing of this agent is written yet* or *this agent already exists and you are correcting it*
— read off the agent, never off which pass is running.

Each pass is a **fresh agent and a fresh session** by design: a pass carrying the whole interview in
its context spends it re-litigating decisions already taken. What crosses a boundary is the
*configuration*, read as settled fact.

### The map first, then the agents

**Alembic edits a domain, not an agent**, so the interview opens on `Intents` and then walks the list.
**A domain is a choice, not an inventory**: the map is the subset of the client's processes they are
installing Morgana to run, not a description of their business.

**All four of an intent's fields are written in the map pass and that is the same argument twice**: a
description is read by the classifier *against every other description*, a label and its sentence by a
user *against every other button* — both correct only side by side.

**The territory is the sixth dimension and it is elicited, not derived.** It is not the `Target`
addressed to a second reader: a `Target` is what this desk handles, a territory is the competence it is
characteristically the one to answer for — what another desk would come to it for rather than answering
*go to them*, which is worth nothing to whoever asked. Elicited whether or not A2A is ever switched on.
It runs **after the toolkit**, the earliest it can be true: a territory the desk's own tools do not
cover is a promise another desk would hold it to.

**The one thing a later pass may write about an intent is its description, once the territory stands.**
The classifier's sentence is the map's to write and the map writes it before any agent exists; the same
subject is then known in sharper words, so the territory pass may say the routing with them — its own
entry only, with every other description read back first, which is what keeps this one distinct.

**`Target` and `Personality` are two passes, not two questions of one.** A `Target` is *dictated*; a
voice is *recognised* — nobody has a ready sentence about how their own staff should sound — so it gets
its own screen and its own form (several adjectives at a time, text box still open).

The `AgentFormatting` pass is the one place the loop stops for the client: letting a finished agent into
the domain is the single decision of the interview that is theirs.

**The fallback intent is not on the map and not in the domain either.** `other` is the *complement* of a
domain rather than a part of one; Morgana's classifier carries it.

### The colleagues, last

`[ConsultsAgent]` **cannot be settled while the agent is being written**: it is a relation and half its
ends do not exist yet. So it is asked once, at the end, over the whole domain — earlier sittings and
uploads included — which makes the closing pass the mirror of the map.

**An edge and the prose it contradicts land together or the edge is a defect.** The model reads a
function offering the colleague and a flat imperative refusing the subject; Morgana's `PeerConsultation`
policy decides that collision by precedence, but an agent whose own prose is overruled every turn is
still a defect — the contradiction is paid in tokens and settled by a model rather than by its author.
So `DeclareConsultation` takes the asking agent's rewritten prose **in the same call**.

**The closing pass is handed each agent's territory and that is what keeps the repair honest.** The
framework appends the colleague's own `ConsultMeFor` to the asking agent's prompt, so the boundary must
state fact about this agent's **own** books and nothing about the other desk. A pass that could not read
the territory had only the colleague's `Target` and `Instructions` to work from, so asked to strike a
refusal it wrote the colleague's *process* into the asking agent's prose instead — the defect the
coherence pass reports as `colleague-out-of-step`.

What that prose must never carry: a rule about when to consult, how briefly, what to expect back or what
to do with the answer — every one of those is a framework rule and a second copy below is a second voice
claiming the same authority.

The client is asked a question about **their own work** — whether the accounts desk really rings the
greenhouse — never which agent should call which, which is machinery they were never shown.

An edge is touched in three places, each doing what the others cannot: **the interview asks** and is the
only place that can rewrite the boundary in the same gesture; **the emit page holds it** and is the only
place a colleague published by *another* Morgana can be declared; **the coherence pass reports** and is
the only one that reads the declaration against the prose and says they disagree.

### The walk: a domain is edited, not only added to

**Correcting is a fourth intention, so it is a fourth door.** Whoever comes through it usually cannot say
where the problem is — they recognise it when they read it — so it opens on the first agent and leafs.

**Leafing costs nothing, because leafing is reading.** `Pages/Revise.razor` calls no model and writes
nothing: an agent read on the way past is never taken out of the configuration. The agent is shown whole
for reading, but no row is its own way in: **one door per agent, always opening at the Target.** The map
is not one of them and cannot be — it settles every intent *against every other*, so reopening it over a
single agent would be the one pass with nothing to compare its work to.

**An edit always opens at the Target and chains forward exactly like composing.** Composing cannot leave
a section wrong, since the toolkit is settled after the Target and against it; an edit that settled only
the section it was opened on could. The `Correcting` row licenses the fast path this needs: where nothing
changed upstream, settle the section with the offered choice **without calling its `Set` tool at all**, so
it survives untouched rather than rewritten in different words — but never before the client has said a
word about the section that was reopened.

### One question, one step

The characteristic failure of an LLM-conducted interview is not a wrong answer, it is **circling**: the
same question again, each phrasing slightly better, until the client stops answering. The fix is doctrine
high in Alembic's `Instructions`, not a patch per pass: every question is a step and an answer advances it
if **anything can be written down** — the rest is inferred, proposed and corrected rather than asked again.
Asking twice tells the client their answer was not good enough.

The toolkit pass states the one instance the doctrine cannot know on its own: **scope is inferred, never
asked per parameter.** The client is asked once, about their setup — what the system already knows about a
user on arrival — and everything on that answer is `context`, everything else `request`. `Shared` is
inferred from what the value *is*.

### Validation runs before the recap

The order is the design: composing a beautiful prompt for a domain that would not start is a way of lying
to the client with something that looks like evidence.

Every check in `DraftValidationService` is decidable by reading the Draft, no model asked. Most restate a
rule the framework enforces at startup and **the duplication is the entire value**: the framework's
exception arrives after the client has packaged, deployed and run; the same sentence here arrives while it
costs nothing to change. Each finding carries a `Because` naming the rule.

### The starter scenarios: templates in, one domain out

Alembic writes the **starting set and no more**: it knows what the agents were designed to do, which is
what a first scenario is made of and nothing about what will actually go wrong, which is every scenario
after it. Running them needs a **source checkout** of Morgana, since PromptHarness boots it in-process.

The split: **which behaviours are worth protecting** is knowledge about agents, true before any client
arrives, settled once as `Distiller/Harness/Templates/*.yaml`. **Which words say them here** is knowledge
about the client's business, so the model derives: replace every `{{…}}`, change nothing else. Asking a
model for "two or three scenarios" was the earlier, wrong shape — it made the model choose which behaviours
matter, the decision it is worst placed to take. Applicability is decided in C# only for what a template
structurally needs (a tool, two tools, a context parameter); everything semantic is the model's.

**Nothing is copied from `PromptHarness/`**, which is entirely infrastructural. That is what makes the suite
100% domain **structurally**: the vocabulary a derivation may use is exactly the union of keys the templates
use.

**A derivation may drop a key and may never add one** — the whole check, enough because the template *is*
the vocabulary. It runs at the emit because it cannot be caught later: `ScenarioLoader` ignores an unmatched
property, so an invented key is dropped without a sound and the scenario loads, runs, passes and asserts
nothing. A scenario that fails still ships, the problem written across its top: a silently missing scenario
costs the client more than a visibly broken one.

### The coherence pass

The half `DraftValidationService` cannot do. Every defect it looks for is **relational**, which is why the
interview cannot close them all — it settles the map together but writes each agent's prose alone. It is
handed the domain's exact words, never a summary — a summary is precisely the step that would smooth an
overlap away before the model saw it.

**The classes are data, not prose and the client picks them.** Each is an entry in the `DomainValidator`
prompt's own `Aspects` declaration, so the checkboxes, the prose the pass is composed from and the `kind`
values it may return are one list read three ways. **An unselected class is not sent at all**, rather than
sent with a line withdrawing it: a rule a prompt states and then takes back is read as a rule with an
exception and the exception is what a model gets wrong.

**Nothing is ticked on arrival and the pass will not run until something is.** A client arrives with one
question about their domain and a report answering six is a table to sift.

**`colleague-out-of-step` is the one class reading something outside the sections** — an agent's colleagues
live in its C#. Its fix is **always prose**: gaining or losing a colleague changes the client's C# and is
theirs to do.

It answers JSON, the one place in Alembic that does and it **advises, never blocks**: a domain expert who
disagrees with it about their own business is usually right.

## The Draft

The single artifact the interview fills, the validator checks, the recap composes and the emit reads.

**Why not the `Records` types directly.** They are the *serialization* model: immutable, complete,
positional. The Draft is the *editing* model and an interview in progress is incomplete by definition — a
tool whose description has not been asked for is a different state from one whose description is
deliberately empty and only a nullable field distinguishes them. **Every nullable string in
`DomainDraft.cs` means "not asked yet."**

**What survives that Alembic does not understand.** `AdditionalProperties` keys other than `Tools` are kept
verbatim in `AgentDraft.UnmodelledProperties` and written back untouched — the round-trip invariant must not
depend on Alembic having a use for every key it meets. The `Tools` key is matched **ordinally**,
deliberately: the framework looks it up in a plain dictionary, so a differently-cased key is invisible there
and must stay invisible here.

**Provenance** (`Imported` / `Revised` / `Authored`) exists so Alembic rewrites only what it owns and can
*report* honestly. It is not what preserves untouched content — that is the round-trip invariant, which
holds regardless.

## The round-trip invariant

**A configuration that goes in comes back out equivalent.** A client uploading ten agents to add an
eleventh gets the other ten back untouched and Alembic does not need to understand them to promise it.

Equivalent, not byte-identical and the difference is what the format means: `AdditionalProperties` is a
*list* Morgana looks keys up **across**, so the grouping carries no information; defaults are written
explicitly; emoji come back as escaped surrogate pairs. Exporting a re-imported Draft is byte-for-byte
stable — a fixed point, so a file that has been through Alembic once stops moving.

`AgentCodeFacts` holds what `agents.json` cannot: namespace, class names, tier, MCP servers and the
colleagues an agent may consult — each a `Records.PeerReference`, the framework's own type (a second
vocabulary for one thing is what makes two projects drift). On import all of it is unknown, so Alembic
proposes class names from the naming convention and flags the record `Inferred`; namespace and tier are left
null rather than guessed, since a confident wrong value is worse than an empty one the interview will ask
about.

## Why the project reference is Morgana.AI, not Morgana.Contracts

The channels reference `Morgana.Contracts` because they exchange wire DTOs. Alembic exchanges none. What it
needs is the **domain model of a Morgana configuration** — `Records.Prompt`, `Records.ToolDefinition`,
`Records.ToolParameter`, `Records.Intent` — plus `IPromptComposerService`. Parsing an uploaded `agents.json`
is therefore free: there is no parallel representation to maintain.

## Traps worth knowing before you meet them

- `IInterviewService` and `IDraftStateService` are **scoped**, everything else singleton: one interview and
  one Draft per circuit.
- `IAgentConfigurationService` is `AgentlessConfigurationService`, empty **by construction**: the domain
  Alembic works on is the **uploaded** one, never one compiled into this process.
- `ILLMService` is a factory **never resolved during startup**, so a working copy without credentials still
  builds, boots and serves the shell; the failure surfaces on the first call.
- `MagicDust` in the `Performance` tier cannot be shortened to `{}` — the JSON provider reads an empty
  object as `null`, the binder drops an element it cannot construct **without raising anything** and the
  whole tier disappears, surfacing a page later as `No tiers configured`.
- The LLM section is named `Morgana:` because `MorganaLLM` reads that path. In-repo, Alembic declares the
  **same `UserSecretsId` as Morgana.Web**, so it runs against whatever this working copy is wired to.

## Build and Run

.NET 10, Blazor Server. `dotnet run` from `Distiller/` serves https://localhost:5005 and needs **no**
Morgana instance running. Docker is profile-gated, so `compose up` skips it.

## Conventions

- Behavioral concerns behind interfaces, default implementation alongside, registration in `Program.cs` —
  the framework's own pattern
- No parallel representation of a Morgana configuration: the `Records` types are the model
- Generated C# obeys the `X.g.cs` / `X.cs` split
- Alembic never writes to, reads from or assumes anything about the client's filesystem
