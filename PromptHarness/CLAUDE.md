# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

PromptHarness is Morgana's testing project and it is aimed at the half of Morgana that has no
compiler: a domain agent *is* its prose — `morgana.json`, `agents.json`, tool descriptions — so most
of what is here answers one question, does the model still do what the prompt tells it to do?
Nothing is mocked there, the LLM is real and a run costs real tokens. Static tests on code are
legitimate here too and cheap by comparison: where a contract is deterministic — a published wire
document, a gate's status codes — it is asserted as plain unit testing, no runs, no threshold, no
judge (see `AgentCardTests`). What holds for both kinds is the last clause: the suite **never becomes
a build/CI gate** — it is on-demand only, because the expensive half cannot be.

It is its own solution (`PromptHarness.slnx`), a sibling of `../Morgana/` and `../Examples/` rather
than a project inside `Morgana.slnx`. It reaches the framework through project references only and
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

# the consulting group (4/4, blocking)
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ConsultingTests"

# the behavioural group (default threshold)
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~BehaviourTests"

# the guard group — requires the boot-time guardrail flag, off by default
Harness__EnableGuardrail=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~GuardTests"

# the rest of the actors group — classifier, channel adaptation, presentation
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ActorTests"

# the card-and-gate group — deterministic, no LLM call, no cost
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~AgentCardTests"

# the summarization group — requires a lowered boot-time reducer trigger, unset by default
Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~SummarizationTests"

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
spans with no exporter/collector in the loop and a stdout tee reading tool log lines. Both are
read-only observers of instrumentation that exists for production reasons, not test-only hooks.

**Configuration.** The harness owns no `Morgana:` config and no secrets of its own — it shares
`Morgana.Web`'s `UserSecretsId`, resolves that project's `appsettings.json` + the shared secrets
store and republishes the result to the host as environment variables (`MorganaHostFixture.
ApplyHostEnvironment`). It then layers a few overrides on top: throwaway SQLite storage path,
telemetry exporters off, rate/dust limiting off, guard rail per `Harness:EnableGuardrail` (default
off) and a random per-run symmetric key for the `harness` JWT issuer. The repo must carry a
`harness` entry under `Morgana.Web/appsettings.json` → `Morgana:Authentication:Issuers`, or the
fixture refuses to start by design.

**Two-layer assertions** (`Infrastructure/Engine/ScenarioDefinition.cs`, `ExpectationChecker.cs`, `LlmJudge.cs`).
Structural checks are deterministic, read only span/log/message data (`expect:` block: tools called,
context reads/writes, quick replies, rich card presence…). The judge layer is LLM-graded natural
language propositions (`judge:` / `judgeNot:`) and sees exactly what a user would see — text, buttons,
and the card as rendered (through the same `RichCardText` the structural layer reads) — never the tool
trace, so it can't justify a verdict on evidence the user never had. Showing it less than the screen is
the mirror error: an agent's Formatting routinely puts the figure that answers the question on the card,
and a judge reading half the screen convicts a response that answered.
The judge is skipped once a turn already fails structurally (saves a live call).

**Scenario groups have different thresholds and mean different things:**
- `ContextHandlingTests` — the blocking group, always 5/5. Protects the context cycle (read before
  asking, ask only on a miss, write on the answer), the closed vocabulary (no invented context
  names) and non-revelation (the user never learns context exists). Failure here is silent — an
  agent that re-asks for something it already knows still looks like it works — so a regression
  reverts the prose rather than lowering the threshold.
- `BehaviourTests` — default threshold (`Harness:DefaultRuns`/`DefaultMinPasses`, 5/4). Protects
  visible presentation: button emission, card rendering, conversation closure.
- `ConsultingTests` — blocking at 4/4, for the same reason as the group above: the failure is
  silent. An agent that quietly stops consulting a colleague still answers, just with less than it
  could have known. Unlike every other group it also depends on a **topology** — the scenarios name
  agents that must still declare `[ConsultsAgent]` of one another — so a failure here has a second
  thing it can mean and the attributes are the first place to look. Both scenarios assert the
  mechanism itself — a colleague reached on demand for a datum only it holds and an exchange that
  leaves the conversation as it found it (`historyExcludesAgents` / `historyUserMessages`, read from
  the persisted history rather than from a judge). The prohibition side of consultation is
  deliberately not swept for: hand a judge a broad "must not say" and it goes hunting through the
  response until it convicts.
- `GuardTests` / `ActorTests` — the **actors** group, aimed at the four framework prompts none of
  the above meaningfully exercise: Guard, Classifier, ChannelAdapter, Presentation. Three production
  mechanisms stood in the way of a straightforward scenario and each got its own workaround rather
  than a shared one: `MorganaHostFixture`'s `Harness:EnableGuardrail` is a whole-process boot flag
  with no per-scenario override, so Guard gets its own test class (`GuardTests`, 5/5 — a moderation
  false negative/positive is safety-adjacent, closer to `ContextHandlingTests`' "failure is silent"
  reasoning than to `BehaviourTests`'); `LLMPresenterService` caches its result process-wide keyed by
  channel name, so Presentation gets a one-shot check in `ActorTests`, never a 5-run scenario;
  `MorganaChannelAdapter` short-circuits on the harness's own full-capability channel, so the one
  scenario meaning to exercise it opts in via `ScenarioDefinition.DegradedChannel`, which opens the
  conversation on `HarnessChannel.DegradedCapabilities`/`DegradedChannelName` instead — a distinct
  channel name, not just different capabilities, to avoid racing the Presentation cache above.
- `SummarizationTests` — the reducer's default trigger (21 non-system messages: `SummarizationTargetCount`
  8 + `SummarizationThreshold` 12) sits far above any scripted conversation's reach, so nothing has
  ever exercised the summarization prompt. Same process-wide-config problem as Guard, same fix: its
  own class, its own filtered run, with `Harness__SummarizationThreshold`/`SummarizationTargetCount`
  lowered just for that invocation. `ExpectSpec.SummarizationOccurred` reads
  `MorganaChatHistoryProvider`'s per-turn log line (the only signal — no span exists) and compares
  the before/after message counts it reports, since the line itself fires every turn a reducer is
  configured, whether or not anything actually shrank.
- `AgentCardTests` — the one group that is neither layer, grades nothing and costs nothing: no LLM
  call, no run threshold, no judge. A card and its gate are **wire contracts** rather than prose, so
  they are asserted deterministically on the JSON and the status codes a foreign implementation would
  actually see. Two contracts and they are complementary halves: the card is served open (a caller
  that must authenticate to learn how to authenticate can never begin) precisely because what it
  points at is not. The gate is asserted on all three of its refusals — no token; a **channel's** own
  valid token, which is refused for being cut for the other door; and a **system's** valid token at a
  desk its `InboundSystems` entry does not name — plus the admission that keeps those from passing for
  the wrong reason, since a gate refusing everything would satisfy all three. Every literal is spelled
  out in the test rather than read from `Constants`: a test comparing a constant against itself
  asserts that a constant equals a constant, while the point is to notice a published document, or a
  door, changing shape under whoever consumes it.

**The journey is the point of the suite.** Every `ScenarioRunner.RunAsync` call writes a row to
`<scenario-id>.md` via `HarnessWriter` — one row per revision phase, with pass rate, token
cost and the provider+model bound to each tier (a token count without the model is meaningless).
The phase name comes from `Harness:Phase` in `appsettings.Harness.json` (or `Harness__Phase` env
override); re-running the same phase **replaces** its row, it never appends — a phase is a state of
the prose, not a count of measurements. `JOURNEY.md` narrates what the movements mean, written by
hand. The directory is `Harness:HarnessDirectory` (default `Harness`, resolved against the project
directory unless absolute) — deliberately **not versioned** (see `.gitignore`): every run is billed,
and a token-count diff is not useful pull-request noise. Read `README.md`'s "The journey" section
before writing to these files by hand.

A prompt revision has exactly three outcomes against the previous journey row and only one is a
pass: threshold held + tokens down (the win); threshold held + tokens up (not a pass — the fixed
payload is resent on every round trip, forever); threshold broken (a contradiction between two
instructions read together — fix the text, never lower the threshold).

**Writing a scenario.** One YAML file per flow under `Scenarios/`, file name == `id`. See the
`ExpectSpec` fields in `Infrastructure/Engine/ScenarioDefinition.cs` for the full structural vocabulary and the
"Writing a scenario" section of `README.md` for the annotated example. `HarnessSmokeTests.
Every_scenario_file_parses` is the only guard that every `.yaml` under `Scenarios/` is well-formed —
it does not run the scenario, just loads it.

## Pointing the suite at a different Morgana

There is no `Directory.Build.props` above this project on purpose — every build setting lives in
`PromptHarness.csproj` itself, so the project carries unchanged across a move. To target a different
Morgana instance (a customer's, an older tag), redirect the two `ProjectReference`s in the `.csproj`
(or drop the `Examples` one and copy a different plugin into the build output's `plugins/`) and
write scenarios naming that instance's own agents.
