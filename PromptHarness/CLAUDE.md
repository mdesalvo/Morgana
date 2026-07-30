# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PromptHarness is the non-regression suite for Morgana's **prompts**, not for its code. A domain
agent in Morgana is its prose — `morgana.json`, `agents.json`, tool descriptions — and prose has no
compiler. Every test here answers one question: does the model still do what the prompt tells it to
do? Nothing is mocked, the LLM is real, and a run costs real tokens. This is not a unit-test project
and never becomes a build/CI gate — it is on-demand only.

It is its own solution (`PromptHarness.slnx`), a sibling of `../Morgana/` and `../Examples/` rather
than a project inside `Morgana.slnx`. It reaches the framework through project references only, and
one of them deliberately excludes the compiled assembly (`../Examples/Examples.csproj`,
`ReferenceOutputAssembly=false`): the example plugin is copied into `plugins/` and discovered by
Morgana's plugin loader at startup, exactly as a real deployment would discover it, never seen as a
compile-time type. That is the black-box boundary made structural.

## Commands

```bash
# everything — live LLM calls, see the cost note below
dotnet test PromptHarness.csproj

# just the rig, before believing any scenario result
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~HarnessSmokeTests"

# the blocking group (context handling, 5/5 threshold)
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"

# the behavioural group (default threshold)
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~BehaviourTests"

# one scenario by id — the scenario id is a Theory InlineData argument, so match on DisplayName, not FQN
dotnet test PromptHarness.csproj --filter "DisplayName~behaviour-rich-card"
```

Always start with `HarnessSmokeTests` when the suite fails wholesale: a broken observer reads exactly
like a prompt regression, because an empty tool list looks the same whether the agent called nothing
or the listener heard nothing.

**Cost discipline.** Every turn is a live LLM call, multiplied by the scenario's run count (default
5), plus one judge call per proposition on structurally-passing turns. The two Inventory scenarios
(`behaviour-rich-card`, `context-no-invented-writes`) run on the `Performance` tier, not
`Efficiency` — an agent's `[RequiresLLMTier]` decides its cost, the suite doesn't get to choose it.
Keep those out of the tight iteration loop; run them at checkpoints. Before running anything, check
whether the answer is already visible in `Harness/JOURNEY.md` or a prior `Harness/<id>.md` row —
don't re-run a measurement that's already recorded for the current phase.

## Architecture

```
PromptHarness (xUnit v3 test process, IsTestProject + OutputType=Exe)
  Infrastructure/Wiring/    — talks to and observes the live host
    ├── MorganaHostFixture ──► boots Morgana.Web's entry point in-process, real Kestrel, ephemeral port
    ├── HarnessChannel     ──► its own channel: REST out (JWT, issuer "harness"), webhook in
    └── TurnObserver       ──► ActivityListener on morgana.agent spans + Console.Out tee on tool logs
  Infrastructure/Engine/    — runs and grades a scenario
    ├── ScenarioRunner      ──► replays a YAML scenario N times, reports pass rate against a threshold
    └── LlmJudge            ──► ILLMService on the cheapest configured tier, natural-language propositions
  Infrastructure/Reporting/ — records the outcome
    ├── HarnessWriter       ──► per-scenario journey row, keyed by Harness:Phase
    └── FailureLog          ──► transcript of the last failing run, deleted once clean
```

**In-process but black-box.** The host runs inside the test process, but the suite only ever talks
to it over HTTP — same REST surface, same JWT gate, same webhook delivery any real channel uses.
Being in-process buys exactly two things a child process couldn't: an `ActivityListener` reading
spans with no exporter/collector in the loop, and a stdout tee reading tool log lines. Both are
read-only observers of instrumentation that exists for production reasons, not test-only hooks.

**Configuration.** The harness owns no `Morgana:` config and no secrets of its own — it shares
`Morgana.Web`'s `UserSecretsId`, resolves that project's `appsettings.json` + the shared secrets
store, and republishes the result to the host as environment variables (`MorganaHostFixture.
ApplyHostEnvironment`). It then layers a few overrides on top: throwaway SQLite storage path,
telemetry exporters off, rate/dust limiting off, guard rail per `Harness:EnableGuardrail` (default
off), and a random per-run symmetric key for the `harness` JWT issuer. The repo must carry a
`harness` entry under `Morgana.Web/appsettings.json` → `Morgana:Authentication:Issuers`, or the
fixture refuses to start by design.

**Two-layer assertions** (`Infrastructure/Engine/ScenarioDefinition.cs`, `ExpectationChecker.cs`, `LlmJudge.cs`).
Structural checks are deterministic, read only span/log/message data (`expect:` block: tools called,
context reads/writes, quick replies, rich card presence…). The judge layer is LLM-graded natural
language propositions (`judge:` / `judgeNot:`) and sees only what a user would see — text, buttons,
card presence — never the tool trace, so it can't justify a verdict on evidence the user never had.
The judge is skipped once a turn already fails structurally (saves a live call).

**Scenario groups have different thresholds and mean different things:**
- `ContextHandlingTests` — the blocking group, always 5/5. Protects the context cycle (read before
  asking, ask only on a miss, write on the answer), the closed vocabulary (no invented context
  names), and non-revelation (the user never learns context exists). Failure here is silent — an
  agent that re-asks for something it already knows still looks like it works — so a regression
  reverts the prose rather than lowering the threshold.
- `BehaviourTests` — default threshold (`Harness:DefaultRuns`/`DefaultMinPasses`, 5/4). Protects
  visible presentation: button emission, card rendering, conversation closure.

**The journey is the point of the suite.** Every `ScenarioRunner.RunAsync` call writes a row to
`<scenario-id>.md` via `HarnessWriter` — one row per revision phase, with pass rate, token
cost, and the provider+model bound to each tier (a token count without the model is meaningless).
The phase name comes from `Harness:Phase` in `appsettings.Harness.json` (or `Harness__Phase` env
override); re-running the same phase **replaces** its row, it never appends — a phase is a state of
the prose, not a count of measurements. `JOURNEY.md` narrates what the movements mean, written by
hand. The directory is `Harness:HarnessDirectory` (default `Harness`, resolved against the project
directory unless absolute) — deliberately **not versioned** (see `.gitignore`): every run is billed,
and a token-count diff is not useful pull-request noise. Read `README.md`'s "The journey" section
before writing to these files by hand.

A prompt revision has exactly three outcomes against the previous journey row, and only one is a
pass: threshold held + tokens down (the win); threshold held + tokens up (not a pass — the fixed
payload is resent on every round trip, forever); threshold broken (a contradiction between two
instructions read together — fix the text, never lower the threshold).

**Writing a scenario.** One YAML file per flow under `Scenarios/`, file name == `id`. See the
`ExpectSpec` fields in `Infrastructure/Engine/ScenarioDefinition.cs` for the full structural vocabulary, and the
"Writing a scenario" section of `README.md` for the annotated example. `HarnessSmokeTests.
Every_scenario_file_parses` is the only guard that every `.yaml` under `Scenarios/` is well-formed —
it does not run the scenario, just loads it.

## Pointing the suite at a different Morgana

There is no `Directory.Build.props` above this project on purpose — every build setting lives in
`PromptHarness.csproj` itself, so the project carries unchanged across a move. To target a different
Morgana instance (a customer's, an older tag), redirect the two `ProjectReference`s in the `.csproj`
(or drop the `Examples` one and copy a different plugin into the build output's `plugins/`), and
write scenarios naming that instance's own agents.
