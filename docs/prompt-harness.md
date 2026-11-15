---
title: "🧪 PromptHarness"
permalink: /prompt-harness/
eyebrow: My companions
tagline: My proving ground
lede: A tome has no compiler. My proving ground replays real conversations against the real AI model and tells you whether my witches still do what their tomes say.
---

## Why I need a proving ground

Change one sentence in a tome and nothing fails to build. Yet that sentence may quietly contradict one of my rules and a witch starts asking for a customer code she already has, or lets go of the conversation too early. You would only find out from a customer.

PromptHarness finds out first. It talks to me exactly like a mirror does, with real conversations against your real AI vendor. Then it checks what happened. Nothing is faked: that is the point.

## Every release of mine is certified

No release of mine leaves the workshop untested. Before it is published, every release passes through my proving ground **on each AI vendor I speak with**, using the `Economy` and `Efficiency` models those vendors offer at that moment. A release is out only once its scenarios hold their thresholds everywhere.

So when you install me with one of my vendors on those two levels, my own rules and the tomes of my example shop have already been proven against the very models you are likely to choose.

## A scenario

Each conversation to replay is a small YAML file. It says what the customer writes at each turn and what must be true afterwards:

```yaml
id: context-cross-agent
description: a customer code given to Billing reaches Contract without re-asking
runs: 5          # replay the whole conversation five times
minPasses: 5     # and require all five to pass

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

## Two kinds of check

| Kind | What it looks at | Good for |
|---|---|---|
| **Facts** (`expect`) | What I really did: which witch answered, which spells she cast, which notes she read or wrote, which buttons and cards she showed | Everything that can be checked exactly, at no cost |
| **A judge** (`judge`, `judgeNot`) | Only what the customer would see on screen: their message and my answer | What no fact can capture, such as *"asks in words without listing options"* |

The judge is an AI model of its own and it sees no more than the customer does. It cannot excuse an answer with evidence the customer never had.

## Thresholds, not luck

An AI model does not answer the same way twice. A tome that works four times in five is not the same as one that works once. So each scenario states how many times it is replayed (`runs`) and how many must pass (`minPasses`), instead of hiding the difference behind retries.

> **A failure is a contradiction in the words.** When a scenario turns red, two sentences disagree when read together. Fix the words. Never lower the threshold to make it pass.
{: .tip}

## Running it

From the root of the repository. It uses the same AI vendors you configured for me.

```bash
# first, check the proving ground itself works
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~HarnessSmokeTests"

# one group of scenarios
dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"
```

Run one group at a time and never two runs at once.

> Every scenario with a witch in it makes real AI calls, so every run costs real money. My proving ground runs when you ask it to, never on every build.
{: .warn}

## When something fails

A failing run keeps its evidence: the whole conversation and every AI call with what it was given and what it answered, the judge's included. Read that before running again. A failure is understood from what is already there, not bought a second time.

## Pointing it at your own tomes

My proving ground knows no witch in advance: it finds them when I wake up, just as I do. To test your own tomes, put your plugin in its `plugins/` folder and write scenarios that name your witches.
