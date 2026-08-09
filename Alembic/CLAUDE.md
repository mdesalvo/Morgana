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
  Pages/_Host.cshtml                  # Blazor Server host page (ServerPrerendered)
  Pages/Index.razor                   # Landing page
  Shared/MainLayout.razor             # Layout wrapper
  wwwroot/css/site.css                # Base styles
```

## DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `ILogger` | Singleton | Several Morgana.AI services take a bare `ILogger`, not `ILogger<T>` |
| `IAgentConfigurationService` | Singleton | `EmbeddedAgentConfigurationService` — finds no embedded `agents.json` and degrades to agentless mode, which is the correct state: the domain Alembic works on is the **uploaded** one, never one compiled into this process |
| `IPromptResolverService` | Singleton | `ConfigurationPromptResolverService` — resolves the framework prompts from `morgana.json`, embedded in Morgana.AI and free through the project reference |
| `IPromptComposerService` | Singleton | `ConfigurationPromptComposerService` — assembles what the model reads; this is what makes the recap a real composed prompt |
| `ILLMService` | Singleton (factory) | Provider selected by `Morgana:LLM:Provider`. **Never resolved during startup**, so a working copy without credentials still builds, boots and serves the shell — the failure surfaces on the first call, with the provider's own message |

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
| 2 | Draft model + import of an uploaded `agents.json` (fixture: `Examples/agents.json`) | |
| 3 | Export + round-trip invariant (an untouched file comes back out intact) | |
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
