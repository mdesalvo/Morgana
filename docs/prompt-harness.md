---
title: PromptHarness
permalink: /prompt-harness/
eyebrow: Companion · quality
lede: Prose has no compiler. The harness is a live and black-box suite that measures whether the model still does what the prompt tells it to do.
---

<div class="pills"><span class="pill">xUnit v3</span><span class="pill you">Live LLM calls</span><span class="pill you">Costs real tokens</span><span class="pill">On demand only</span></div>

## Why it exists

An agent *is* its prose. A sentence added to `morgana.json` can quietly contradict a sentence in `agents.json` and nothing fails to compile. The symptom is an agent that re-asks for something it already knows or hands a conversation back too early. The harness replays real conversations against a real model many times and says whether the behaviour held.

It is not a unit-test project and it will never become one. Nothing is mocked. It never becomes a build or CI gate either, because the expensive half cannot.

## How it hangs together

<pre class="mermaid">
graph TB
  subgraph T["PromptHarness · the test process"]
    F["MorganaHostFixture<br/>Morgana.Web in-process"]
    H["HarnessChannel<br/>webhook · full capabilities"]
    O["TurnObserver<br/>spans and tool logs"]
    J["LLMJudge<br/>Efficiency tier"]
    R["ScenarioRunner<br/>replays N times"]
  end
  Y["Scenario YAML"] --> R
  R --> H
  H -->|"REST · JWT iss=harness"| F
  F -->|webhook| H
  F -.spans.-> O
  R --> O
  R --> J
  F <--> L["Real language model"]
  F <--> G["Gardening MCP :5006"]
</pre>

The host runs **in the test process** but is only ever addressed **over HTTP**: the same REST surface and JWT gate and webhook delivery any channel uses. Running in-process buys two read-only observers: an `ActivityListener` that reads spans with no exporter and a tee on the tool log lines.

The harness owns no secrets. It resolves the same configuration and user secrets as Morgana.Web, so it runs against whatever provider and tiers your Morgana is wired to.

## A scenario

One YAML file per flow. Each turn says what the user does and what must hold.

```yaml
id: context-cross-agent
description: a customer code given to Billing reaches Contract without re-asking
runs: 5          # replay the whole flow five times
minPasses: 5     # and require every one to pass

turns:
  - say: "I'm customer P994E, show me my invoices"
    expect:
      agent: BillingAgent
      toolsCalled: [GetInvoices]
      contextWrites: [customerCode]
  - say: "And what does my care plan cover?"
    expect:
      agent: ContractAgent
      contextReads: [customerCode]
    judge:
      - "The response summarises what the care plan covers."
    judgeNot:
      - "The response asks the user for their customer code."
```

## Two layers of assertion

| Layer | Reads | Good for |
|---|---|---|
| **Structural** | Spans and logs and persisted messages. Deterministic | Which agent answered and which tools were called and what context was read or written and quick replies and cards |
| **Judge** | What the user would see: their message and the response's text and buttons and card | What no structural check reaches: *asks in prose without enumerating options* |

The judge never sees the tool trace, so it cannot justify a verdict from evidence the user never had. **One proposition and one act**: a conjunction of two claims is two propositions.

## Thresholds, not retries

A prompt that works four times in five is materially different from one that works once. Each scenario states its flakiness budget as `runs` and `minPasses` instead of hiding it in a retry.

> **A red is a contradiction.** A scenario that regresses is a symptom of two instructions that disagree when read together. Fix the text. Never lower the threshold. The context-handling group runs at 5 of 5 and is blocking.
{: .rule}

## Running it

```bash
# the rig first: never believe a scenario before it is green
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~HarnessSmokeTests"

# deterministic groups: no model and no cost
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~StartupValidationTests"
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~AgentCardTests"

# one behavioural group
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"

# one scenario by display name
dotnet test PromptHarness/PromptHarness.csproj --filter "DisplayName~behaviour-rich-card"
```

Never combine filters: several groups carry a boot knob the others must not see. Never run invocations in parallel: they share one `bin`.

## The evidence trail

- **A failing run keeps its evidence.** `Harness/failures/<id>.log` holds the transcript and `<id>.model.log` every model call of the failing runs, the judge's included: what each was handed and what it answered. A red is read there and never bought again with another run
- **The journey.** Each scenario writes a row per revision phase with its pass rate and token cost. A revision has three outcomes against the previous row and only one is success: threshold held with tokens down. Held with tokens up is not a pass, since the prose got longer and is resent on every round trip of every turn

## Groups at a glance

| Kind | Examples | Cost |
|---|---|---|
| Deterministic | Startup validation · agent cards · peer federation · conversation API · tool result checks | None |
| Live without a judge | ToolGuard over calibration sets | Model calls |
| Behavioural | Context handling · turn presentation · classifier routing · channel adaptation · summarisation | Model calls and the judge |
| Adversarial | Hijacking by a poisoned MCP server or partner | Model calls |

## Pointing it at your own domain

The harness is an instrument pointed *at* a Morgana, not a part of one. To test yours redirect the project references, or copy your plugin into the output's `plugins/` and give it scenarios that name your agents. It compiles without ever seeing an agent type, exactly as Morgana sees them: discovered at startup.
