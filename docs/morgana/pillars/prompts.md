---
title: "3 · Prompts"
permalink: /morgana/pillars/prompts/
eyebrow: Pillar 3 of 6
lede: Prompts are project artifacts. They are versioned and layered like code and they are where an agent's behaviour actually lives.
---

## Two files and two readers

No instruction to a model is hardcoded in C#. Prompts resolve by ID through a prompt resolver and come from two places.

| File | Owned by | Holds |
|---|---|---|
| `morgana.json` | The framework | Morgana's own character, the global policies, the prompts of the guards, the classifier, the presentation and the channel adapter |
| `agents.json` | Your domain | The intents users can ask for and one prompt per agent |

A prompt ID must be unique across both. A plugin that tries to redefine a framework prompt or the reserved intent `other` is refused at startup.

## What an agent's prompt contains

Each domain agent is authored in five sections.

| Section | Answers | Rendered into the agent's prompt |
|---|---|---|
| `Target` | What does this agent do and where does it stop? | Yes |
| `Personality` | Which facet of Morgana is it? | Yes |
| `Instructions` | How does it go about the work? | Yes |
| `Formatting` | How does it present what its tools return? | Yes |
| `Territory` | What is it characteristically the one to be asked about? | **No.** It is read by *other* agents and travels on the A2A card |

Here is the start of Billing from the example shop.

```json
{
  "ID": "Billing",
  "Target": "You keep the accounts book of The Greenhouse & Nursery.",
  "Personality": "You are a formal and pragmatic witch, the one who keeps the nursery's accounts. Be exact with money: a garden is paid for in real coin.",
  "Instructions": "Your accounts tools are read-only. You can show invoices, what is still outstanding and payment history, and nothing else: you cannot take a payment or send a reminder, so never offer to.",
  "Territory": "The money side of this customer's dealings with the nursery: the invoices issued to them, what is still outstanding and the payments actually received."
}
```

## Layered personality

Every conversation keeps one global character (Morgana's) and lets each agent add a facet to it. Billing is a pragmatic witch and Contract is a patient and empathetic one. Both remain recognisably Morgana. The result is a consistent brand with variation by expertise.

The two layers are fenced when they are composed. Both carry the same four section labels, so without fences a prompt would show `[TARGET]` twice with nothing saying whose it is. The composer puts the labels and fences in place. No JSON writes them and none can be forgotten.

## Global policies

The framework's layer carries **global policies**, rendered into every agent in priority order. They are the rules that hold for every domain, written once.

| Policy | Governs |
|---|---|
| P0 · ContextHandling | Look in memory before asking the user |
| P1 · QuickReplyDoctrine | The master rule for buttons that the other quick-reply policies apply |
| P2 · TurnEnding | When an agent hands the conversation back |
| P3 · SessionContinuation | How an agent stays in service across turns |
| P4 · ToolUsage | How tools are called |
| P5 · ToolGrounding | What an agent may state and what it may not |
| P6 · MandatoryTextualResponse | A turn always ends with prose |
| P7 · RichCardUsage | When a card is the right answer |
| P8 · PeerConsultation | Consulting colleagues. Rendered only for agents inside the A2A topology |

Next to the policies sit **injections**: templates with a single splice site each, such as the per-turn declaration of variables the session already holds. An injection is a fact or a template and never a rule.

## The domain layer is subordinate

This is what makes the system cheap to author. An agent that restates a framework rule is not reinforcing it. It is a second voice claiming the same authority and the two may drift apart. So a domain agent says the **least and the most specific** thing possible. If two agents need the same sentence, that is a gap in the global policies and it is filled above, never repaired below.

> **Contradictions are the characteristic defect.** Not an exception or a crash: two instructions that disagree when read together, typically one in an agent and one in a policy. The cure is structural. State a unifying doctrine high in the order and let specific rules read as instances of it. Never lower a test threshold to make a scenario pass. The [PromptHarness]({{ '/prompt-harness/' | relative_url }}) measures exactly this.
{: .rule}

## Outside the prose

Two things can sabotage a correct prompt and are invisible from `agents.json`. Rule them out first.

- **Model tier.** A dense, layered prompt needs a capable model. `Economy` amplifies contradiction-following failures where `Efficiency` would not.
- **Rendering code.** A card showing raw `**` is a channel bug that no prompt can fix.

## No redeployment

Because prompts are data, a prompt engineering team can iterate without touching application code. Behaviour changes flow from changes to JSON, versioned and reviewable like any source file.
