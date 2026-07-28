# PromptHarness — non-regression harness

A live, black-box suite that measures **prompt behaviour**. It exists because Morgana's substance is
prose — `morgana.json`, `agents.json`, the tool descriptions — and prose has no compiler. Every
assertion here answers one question: *does the model still do the thing the prompt tells it to do?*

It is not a unit-test project and will never become one. Nothing is mocked, the LLM is real, and a
run costs real tokens.

## Where it lives

Its own solution (`PromptHarness.slnx`), sibling of `Morgana/` and `Examples/` at the
repository root — not a project inside `Morgana.slnx`. The harness is an instrument pointed **at** a
Morgana instance, not a part of one, and the distinction is operational rather than tidy: the suite
has to be able to run against a Morgana it did not ship with — an older tag of this one, or someone
else's — and a project living inside the framework's solution quietly assumes it never will.

```
Morgana/                    the framework (Morgana.slnx)
Examples/                   the example plugin (Examples.slnx)
PromptHarness/              this harness (PromptHarness.slnx)
```

Everything it needs from the framework arrives by project reference, and there are exactly two:

| Reference | Why | Assembly referenced |
|---|---|---|
| `..\Morgana\Morgana.Web` | the fixture boots its entry point in-process | yes |
| `..\Examples` | the specimens under test, built into `plugins/` | **no** (`ReferenceOutputAssembly=false`) |

The second is the black-box boundary made structural: the harness compiles without ever seeing an
agent type, exactly as Morgana sees them — discovered from `plugins/` at startup.

To point the suite at a **different** Morgana, redirect those two references (or drop the second and
copy your own plugin into the output's `plugins/`) and give it scenarios that name your agents. There
is no `Directory.Build.props` above this project: every build setting is stated in the `.csproj`, so
it carries across a move.

## How it hangs together

```
PromptHarness (test process)
  ├── MorganaHostFixture ──► Morgana.Web entry point, in-process, real Kestrel on an ephemeral port
  ├── HarnessChannel     ──► channelName "harness", deliveryMode "webhook", full capabilities
  │                          REST out (JWT iss=harness) · webhook in (own ephemeral port)
  ├── TurnObserver      ──► ActivityListener on morgana.agent  → agent.tools_invoked
  │                          Console.Out tee on MorganaTool logs → context reads/writes
  ├── LlmJudge          ──► ILLMService.CompleteWithSystemPromptAsync (cheapest configured tier)
  └── ScenarioRunner    ──► replays a YAML scenario N times, reports passes against a threshold
```

The host runs **in the test process** but is only ever addressed **over HTTP**: the same REST
surface, the same JWT gate, the same webhook delivery any channel uses. In-process buys exactly two
things a child process could not — an `ActivityListener` that reads spans with no exporter in the
loop, and a tee on stdout that reads tool log lines. Both are read-only observers of instrumentation
that already exists for production reasons.

The harness channel declares the **full** capability profile with no length budget, so
`MorganaChannelAdapter` short-circuits and scenarios measure undegraded output. Degradation stays
Rune's quadrant of the channel matrix.

## Configuration and secrets

The harness owns **no `Morgana:` configuration and no secrets**. It declares the same
`UserSecretsId` as `Morgana.Web` (`374228be-…`), resolves that project's `appsettings.json` plus the
shared secrets store, and republishes the result to the host as environment variables. Whatever
provider, tier and key your Morgana is currently wired to, the suite runs against it.

On top of that it overrides, per run:

| Override | Why |
|---|---|
| `ConversationPersistence:StoragePath` → temp dir | throwaway SQLite databases, deleted on teardown |
| `OpenTelemetry:Exporters[*]:Enabled` → false | the in-process listener needs no collector |
| `RateLimiting:Enabled`, `DustLimiting:Enabled` → false | a repeated-run suite would throttle itself |
| `ActorSystem:EnableGuardrail` → `Harness:EnableGuardrail` | off by default: no scenario asserts moderation, and every guarded turn is an extra LLM call |
| `Authentication:Issuers[harness]:SymmetricKey` → random | minted per run, never written to disk |

The only thing the repository must carry is the `harness` entry in
`Morgana.Web/appsettings.json` → `Morgana:Authentication:Issuers`, with the usual
`_SECURE_OVERRIDE_` placeholder. Without it the harness refuses to start, by design: it authenticates
as its own channel and will not run against an instance that has not declared it.

## Running it

```bash
# everything (live LLM calls — see the cost note)
dotnet test PromptHarness/PromptHarness.csproj

# just the rig, before believing any scenario result
dotnet test … --filter "FullyQualifiedName~HarnessSmokeTests"

# the blocking group
dotnet test … --filter "FullyQualifiedName~ContextHandlingTests"

# one scenario — by DisplayName: the scenario id is a theory argument, not part of the FQN
dotnet test … --filter "DisplayName~behaviour-rich-card"
```

Start with `HarnessSmokeTests` whenever the suite fails wholesale: a broken observer reads exactly
like a prompt regression, because an empty tool list looks the same whether the agent called nothing
or the listener heard nothing.

**Cost.** Every turn is a live LLM call, multiplied by the scenario's run count, plus one judge call
per proposition on structurally-passing turns. The default is 5 runs.

**The tier is not the suite's to choose.** Each agent binds to its die through
`[RequiresLLMTier]`, so a scenario costs whatever the agent it exercises costs — and forcing it
otherwise would mean measuring a configuration nobody runs. With the example plugin that means:

| Scenario | Agent | Tier |
|---|---|---|
| `context-cycle-on-miss`, `context-cycle-on-hit`, `context-cross-agent`, `behaviour-conversation-closure`, `behaviour-turn-continuation-operand` | Billing, Contract | `Efficiency` |
| `context-closed-vocabulary-monkeys` | Monkeys | `Efficiency` |
| `behaviour-rich-card`, `context-no-invented-writes` | Inventory | **`Performance`** |

Everything Morgana runs on its own account — guard, classifier, presenter — plus the judge, always
goes to the cheapest configured tier. So the two Inventory scenarios dominate the bill: at 5 runs
each they are a handful of `Performance` turns against a suite that is otherwise `Efficiency`
throughout. Keep them out of the tight iteration loop and run them at checkpoints.

Running the rest against `Efficiency` is also a useful stress test in its own right: it is the tier
that amplifies contradiction-following failures.

## The journey

Every scenario run writes `Baseline/<id>.md`: one **row per revision phase**, carrying the pass rate
and the token cost of a run, with the provider and the model bound to each tier recorded alongside (a
token count without them measures nothing). `v0-vanilla` is the original assessment — the prose as it
stood before A2 — and each phase after it shows what the revision bought or cost. `Baseline/JOURNEY.md`
carries what the movements *mean*, including the regressions and the changes to the measuring
instrument itself.

The phase name comes from `Harness:Phase`; bump it in `appsettings.Harness.json` when starting a new one, or
override a single run with `Harness__Phase=A2.3`. **Re-running a phase replaces its row** rather than
appending another: a phase is a state of the prose, not a count of how many times it was measured.

`Baseline/` is a local measurement log, not a repository artefact — it is not versioned. Every run is
billed, and a token-count diff is not useful pull-request noise; the movement lives inside the
artefact itself (one row per phase), so it stays comparable whether or not anyone diffs commits. The
directory is `Harness:BaselineDirectory` in `appsettings.Harness.json` (default `Baseline`, resolved
against the project directory unless given an absolute path) — point it outside the checkout if
several people run the suite and should not overwrite each other's rows.

A prompt revision has three possible outcomes against the previous row, and only one of them is
success:

- threshold held, tokens down → the outcome A2 is aiming for;
- threshold held, tokens up → not a pass. The prose got longer, and the fixed payload is resent on
  every round trip of every turn, forever;
- threshold broken → a contradiction between two instructions read together. Fix the text, do not
  lower the threshold.

`llm calls` is worth reading as carefully as the token counts: it is the multiplier. The composed
system prompt plus the tool schemas is resent in full on **every** round trip, so a turn that takes
four tool-loop iterations pays the fixed payload four times.

## Writing a scenario

One YAML file per flow under `Scenarios/`, named after its `id`.

```yaml
id: my-scenario
description: what this protects, in one sentence
runs: 5            # default: Harness:DefaultRuns
minPasses: 4       # default: Harness:DefaultMinPasses

turns:
  - say: "what the user types"
    expect:
      agent: BillingAgent
      agentCompleted: false
      quickReplies: none          # none | any | count:N | min:N
      quickReplyIds: [continue_agent]
      noQuickReplyIds: [exit_agent]
      richCard: absent            # absent | present
      toolsCalled: [GetContextVariable]
      toolsNotCalled: [GetInvoices]
      toolsCalledFirst: [GetContextVariable]   # prefix of the invocation order
      contextReads: [userId]
      contextWrites: [userId]
      noContextWrites: true
      noContextAccess: true
      contextVocabulary: [userId] # every name touched must appear here
      textNotEmpty: true
      textNotContains: ["#INT#"]
    judge:                        # propositions an LLM must find TRUE
      - "The response asks the user for an identifier."
    judgeNot:                     # …and FALSE
      - "The response mentions variables, context or memory."
```

Two layers, and the split is deliberate: **structural** assertions are deterministic and read only
span, log and message data; the **judge** is for what no structural assertion can reach ("asks in
prose without enumerating options"). The judge sees only what a user would see — text, buttons,
whether a card was rendered — never the tool trace, so it cannot justify a verdict from evidence the
user never had.

**Thresholds.** Repetition with a pass threshold is the only honest shape when the system under test
is a language model: a prompt that works four times in five is materially different from one that
works once, and a single run cannot tell them apart. The threshold makes each scenario's flakiness
budget explicit instead of hiding it in a retry.

**The context-handling group runs at 5/5 and is blocking.** Its three properties — the cycle, the
closed vocabulary, non-revelation — are contract, not behaviour, and their failure mode is silent:
an agent that re-asks for something it already knows still looks like it works. A regression there
stops the prompt revision and the text goes back, rather than the threshold going down.

More generally: a scenario that regresses is a symptom of a **contradiction** between two
instructions read together, not a threshold to lower.
