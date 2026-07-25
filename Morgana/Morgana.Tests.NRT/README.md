# Morgana.Tests.NRT — non-regression harness

A live, black-box suite that measures **prompt behaviour**. It exists because Morgana's substance is
prose — `morgana.json`, `agents.json`, the tool descriptions — and prose has no compiler. Every
assertion here answers one question: *does the model still do the thing the prompt tells it to do?*

It is not a unit-test project and will never become one. Nothing is mocked, the LLM is real, and a
run costs real tokens.

## How it hangs together

```
Morgana.Tests.NRT (test process)
  ├── MorganaHostFixture ──► Morgana.Web entry point, in-process, real Kestrel on an ephemeral port
  ├── NrtChannel        ──► channelName "nrt", deliveryMode "webhook", full capabilities
  │                          REST out (JWT iss=nrt) · webhook in (own ephemeral port)
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

The NRT channel declares the **full** capability profile with no length budget, so
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
| `ActorSystem:EnableGuardrail` → `Nrt:EnableGuardrail` | off by default: no scenario asserts moderation, and every guarded turn is an extra LLM call |
| `Authentication:Issuers[nrt]:SymmetricKey` → random | minted per run, never written to disk |

The only thing the repository must carry is the `nrt` entry in
`Morgana.Web/appsettings.json` → `Morgana:Authentication:Issuers`, with the usual
`_SECURE_OVERRIDE_` placeholder. Without it the harness refuses to start, by design: it authenticates
as its own channel and will not run against an instance that has not declared it.

## Running it

```bash
# everything (live LLM calls — see the cost note)
dotnet test Morgana/Morgana.Tests.NRT/Morgana.Tests.NRT.csproj

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
per proposition on structurally-passing turns. The default is 5 runs. Run groups during
iteration and the whole suite only at checkpoints. Running against the `Efficiency` tier is also a
useful stress test: it is the tier that amplifies contradiction-following failures.

## Writing a scenario

One YAML file per flow under `Scenarios/`, named after its `id`.

```yaml
id: my-scenario
description: what this protects, in one sentence
runs: 5            # default: Nrt:DefaultRuns
minPasses: 4       # default: Nrt:DefaultMinPasses

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
