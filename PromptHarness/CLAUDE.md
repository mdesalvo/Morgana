# PromptHarness — Morgana's non-regression harness

`README.md` beside this file is the full account: the rig, the journey, the annotated scenario
format. This file is what has to be known **before** running anything or touching the build.

## What this is

The half of Morgana that has no compiler. A domain agent *is* its prose, so most of what is here
answers one question: **does the model still do what the prompt tells it to do?** Nothing is mocked,
the LLM is real, a run costs real tokens.

Deterministic contracts are asserted here too, as plain unit testing — no runs, no threshold, no
judge — where what is under test is a wire document or a gate's status codes.

**The suite never becomes a build or CI gate.** On-demand only, because the expensive half cannot be.

## Two things to know before touching the build

**The example plugin deploys to `domain-plugins/`, not `plugins/`.** `plugins/` is always scanned on
top of whatever configuration declares, so a domain deployed there can be added to but never left
out — while an installation reads the **first** `agents.json` it finds and holds exactly one domain.
The federation run needs to **swap** the domain rather than extend it, which is only possible while
the implicit directory stays empty.

**Its own solution**, sibling of `../Morgana/` and `../Examples/`. Two project references reach the
framework, one of them deliberately excluding the compiled assembly (`Examples`,
`ReferenceOutputAssembly=false`): the plugin is copied into the output and discovered at startup
exactly as a real deployment would discover it, never seen as a compile-time type. That is the
black-box boundary made structural — which is also why there is no `Directory.Build.props` above this
project: every build setting lives in the `.csproj`, so it carries unchanged across a move.

## Commands

Thirteen test classes. **Never combine filters**: four groups carry a process-wide boot knob the
others must not see. Never parallelise invocations either — they share one `bin`/`obj`.

```bash
# the rig, before believing any scenario result
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~HarnessSmokeTests"

# deterministic — no model, no cost
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~StartupValidationTests"
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~AgentCardTests"
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PeerFederationTests"

# blocking
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ConsultingTests"

# behavioural
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~BehaviourTests"
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ActorTests"
dotnet test PromptHarness.csproj --filter "FullyQualifiedName~ServedConsultationTests"

# boot-flagged — one knob each, never together
Harness__EnableGuardrail=true dotnet test … --filter "FullyQualifiedName~GuardTests"
Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 dotnet test … --filter "FullyQualifiedName~SummarizationTests"
Harness__DustBudgetPerConversation=15 dotnet test … --filter "FullyQualifiedName~DustTests"
Harness__FederatedPeer=true dotnet test … --filter "FullyQualifiedName~FederationTests"

# one scenario — the id is a Theory argument, so match DisplayName, never the FQN
dotnet test PromptHarness.csproj --filter "DisplayName~behaviour-rich-card"
```

**Read the test count in every summary, not only the verdict.** A filter matching no class exits 0
having run nothing: a group reported green having run zero tests is the failure mode that looks
exactly like success.

Start from `HarnessSmokeTests` whenever the suite fails wholesale: a broken observer reads exactly
like a prompt regression, because an empty tool list looks the same whether the agent called nothing
or the listener heard nothing.

**Cost discipline.** Every turn is a live call, multiplied by the run count (default 5), plus one
judge call per proposition on structurally-passing turns. An agent's `[RequiresLLMTier]` decides its
cost — the suite does not get to choose it — so the Inventory scenarios run on `Performance` while
the rest run on `Efficiency`. Keep those out of the tight loop. Before running anything, check
whether the answer is already recorded in `Harness/JOURNEY.md` or a prior `Harness/<id>.md` row.

## The groups and what a failure in each means

| Group | Threshold | What it protects |
|---|---|---|
| `ContextHandlingTests` | **5/5, blocking** | The context cycle, the closed vocabulary, non-revelation. Failure is **silent**: an agent re-asking for what it knows still looks like it works |
| `ConsultingTests` | **4/4, blocking** | A colleague reached on demand for a datum only it holds; an exchange leaving the conversation as it found it. Silent the same way. Also depends on a **topology**, so a failure here has a second meaning: check the `[ConsultsAgent]` attributes first |
| `BehaviourTests` | 5/4 | Visible presentation: buttons, cards, closure |
| `GuardTests` | 5/5 | Moderation. A false negative is safety-adjacent, so it sits with the blocking reasoning rather than with presentation |
| `ActorTests` | mixed | The framework prompts nothing else exercises: classifier, channel adaptation, presentation (a one-shot check, since the presenter caches process-wide by channel name) |
| `ServedConsultationTests` | — | This installation answering a partner: which conversation, what it cost, how many exchanges are admitted |
| `SummarizationTests` | — | The reducer's own prompt, unreachable at the default 21-message trigger |
| `DustTests` | — | The budget thresholds, crossed in order. Evidence-driven rather than turn-pinned: how many turns it takes is a real token measurement |
| `AgentCardTests` · `StartupValidationTests` · `PeerFederationTests` | none | Wire contracts and boot refusals, asserted deterministically. **Every literal is spelled out in the test** rather than read from `Constants`: a test comparing a constant against itself asserts that a constant equals a constant, while the point is to notice a published document changing shape under whoever consumes it |
| `FederationTests` | — | Two Morganas, one consulting the other — the only test where the card is written by a Morgana, read by a Morgana and the token one mints is proven by the other |

**The judge sees exactly what a user would see** — text, buttons, the card as rendered — never the
tool trace, so it cannot justify a verdict on evidence the user never had. Showing it less than the
screen is the mirror error: an agent's `Formatting` routinely puts the answering figure on the card,
so a judge reading half the screen convicts a response that answered. It is skipped once a turn
already fails structurally.

**One proposition, one act.** A list of synonyms for one act is fine; a conjunction of two claims is
not — neither is a comparative. A proposition needing a carve-out to be fair was written wrong:
fix the act, never coach the judge. This holds for `judgeNot` above all — a broad prohibition puts
the judge on a hunt, a model on a hunt finds something and every false red spends the suite's trust.

## The journey

Every run writes a row to `Harness/<scenario-id>.md`: one row **per revision phase**, with pass rate,
token cost and the provider plus model bound to each tier — a token count without them measures
nothing. Re-running a phase **replaces** its row rather than appending: a phase is a state of the
prose, not a count of how many times it was measured. `JOURNEY.md` narrates what the movements mean,
written by hand. The directory is deliberately **not versioned**: every run is billed and a
token-count diff is not useful pull-request noise.

A prompt revision has three possible outcomes against the previous row, of which one is a pass:
threshold held with tokens down (the win); threshold held with tokens up (**not** a pass — the fixed
payload is resent on every round trip, forever); threshold broken (a contradiction between two
instructions read together — **fix the text, never lower the threshold**).

## Configuration

The harness owns **no `Morgana:` configuration and no secrets**. It shares `Morgana.Web`'s
`UserSecretsId`, resolves that project's settings plus the shared store and republishes the result
to the host as environment variables. On top it overrides, per run: a throwaway storage path;
exporters off; rate and dust limiting off; the guard rail per `Harness:EnableGuardrail`; a random key
for the `harness` issuer; one partner appended, admitted to a single desk.

The repository must carry the `harness` entry under `Morgana:Authentication:Issuers` or the fixture
refuses to start — by design: it authenticates as its own channel.
