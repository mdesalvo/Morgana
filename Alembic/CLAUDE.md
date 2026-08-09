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

### Alembic's own prose

Alembic's conducting prompt lives in an `alembic.json` of **identical shape** to an agent's
(Target / Instructions / Personality / Formatting) — dogfooding: whoever tunes Alembic does the job
Alembic teaches.

It is **not** composed through `IPromptComposerService`. The framework layer in `morgana.json` is the
law of a *channel turn* — QuickReplyDoctrine, TurnContinuation, RichCardUsage, ToolGrounding — and
Alembic has no channel, no Guard, no Classifier and no turn in that sense. Stacking those policies on
it would issue instructions about things that do not exist in its world, which is the most direct way
to manufacture the non-local contradictions this whole project exists to avoid. Same *form*, own prompt.

For the same reason the variant "Alembic is a Morgana agent with its own intent" was rejected: it
would drag in the entire Akka pipeline for an interview FSM that has nothing in common with it.

### Regeneration contract

Generated C# is split across two files per class:

| File | Owner | Rule |
|---|---|---|
| `X.g.cs` | Alembic | attributes, constructor, `partial` signatures — **always overwrite** |
| `X.cs` | the client | the working mock body, then the client's real integration — **written once, never touched again** |

The split does double duty: it is the non-destructive-regeneration mechanism, *and* it is the line
between what is templated (deterministic, so a re-run produces no spurious diff) and what is authored
by the LLM (a plausible domain mock, which a template writes badly).

Because Alembic never sees the client's tree, this convention travels **inside the downloaded
archive** and holds even where Alembic is absent. It does not need enforcing either: a signature that
drifts is already a startup failure, since `MorganaToolAdapter.AddTool` validates delegate against
definition. Alembic's job is only to surface it earlier, via an unconditional **migration report** of
what changed against the uploaded `agents.json`.

### The recap is the real prompt

The interview recap is **the composed prompt the model will actually read**, not a summary of the
client's answers. This is possible because `IPromptComposerService.ComposeAgentInstructionsAsync`
takes the domain prompt as a parameter: the `Records.Prompt` is built in memory from the Draft and
needs to exist nowhere on disk.

## Project Structure

```
Alembic/
  Alembic.slnx                        # Solution (sibling of Morgana.slnx, Cauldron.slnx, …)
  Alembic.csproj                      # .NET 10 Web SDK, ProjectReference → Morgana.AI
  Directory.Build.props               # Build settings, version, package metadata
  Directory.Build.targets             # Regenerates the root .env.versions on each build
  Alembic.Dockerfile                  # Multi-stage container build (root context)
  appsettings.json                    # Morgana:LLM section (Performance tier only)
  Properties/launchSettings.json      # Dev profile: https://localhost:5005
  Program.cs                          # DI wiring and app pipeline
  App.razor                           # Blazor root component
  _Imports.razor                      # Shared @using directives
  Model/
    DomainDraft.cs                    # The Draft: DomainDraft, IntentDraft, AgentDraft, ToolDraft, …
    AgentsConfigurationFile.cs        # The on-disk shape of an agents.json (Morgana's own Records inside)
    Provenance.cs                     # Imported / Revised / Authored
  Interfaces/
    IDraftImportService.cs            # Uploaded agents.json → Draft
    IDraftExportService.cs            # Draft → agents.json (the round-trip invariant)
    IDraftSerializationService.cs     # Draft ⇄ alembic-draft.json (the interview's save file)
    IDraftStateService.cs             # The Draft under construction (per circuit)
  Services/                           # Default implementations of the above
  Pages/_Host.cshtml                  # Blazor Server host page (ServerPrerendered)
  Pages/Index.razor                   # Landing page
  Pages/Import.razor                  # Upload an agents.json, see the parsed Draft, download it back
  Shared/MainLayout.razor             # Layout wrapper
  wwwroot/css/site.css                # Base styles
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
| 4 | Deterministic validation + recap as the real composed prompt — *first shippable milestone: useful without any interview* | |
| 5 | Interview, functional pass (`alembic.json`, FSM, intent + agent prose) | |
| 6 | Interview, toolkit pass + return pass (`Instructions`/`Formatting` speak about tools, so they come last) | |
| 7 | C# asset emit + migration report — *turnkey* | |
| 8 | PromptHarness starter scenarios + cross-agent coherence pass | |

## Conventions

- Behavioral concerns behind interfaces, default implementation alongside, DI registration in
  `Program.cs` — same pattern as the framework
- No parallel representation of a Morgana configuration: the `Records` types are the model
- Generated C# obeys the `X.g.cs` / `X.cs` split described above
- Alembic never writes to, reads from, or assumes anything about the client's filesystem
