---
title: Morgana at a glance
permalink: /morgana/
banner: brand/banner.jpg
eyebrow: Morgana
lede: A conversational AI framework where the domain is written in prose and the machinery around it is already built. This page is the map. The rest of the manual is the territory.
---

## A good witch and her grimoire

<figure class="motto">
  <blockquote>A magical witch assistant equipped with an enchanted grimoire powered by AI, yet shaped by you: <strong>your agents, your prompts, your tools</strong>…woven into her spells.</blockquote>
</figure>

Morgana plays a **good witch**. Every answer a user receives comes in her voice, with her care for the person asking and her honesty about what she can and cannot do. The books she works from are **written by you and for you**. Each agent is a page of her grimoire: a few paragraphs of prose in your words about your business and a few tool contracts that let her touch your systems. The magic itself is the framework and the knowledge is yours.

That is why the project is full of witchcraft names. **Cauldron** is the vessel she works in and **Grimoire** the book in the terminal. **Rune** is the small mark and **Alembic** the apparatus that distils a domain expert's knowledge into new pages. **Dust** is the budget of magic each conversation may spend.

## The idea in one paragraph

A language model does not need an application server written around every business question. It needs a clear job description, the right tools and a few firm rules. Morgana takes that seriously. A domain expert describes the job in sentences. A developer adds thin tool contracts where the agent must reach a system. Morgana handles everything else: who may speak, what the message is about, which agent answers, what it remembers, how the answer looks on each screen and what it costs.

> **Agents are prose, not code.** Building or tuning an agent is mostly the work of writing clear and consistent sentences. Each sentence is an instruction that a model executes, so two sentences that contradict each other are a bug. The framework is built to keep that surface small.
{: .rule}

## The life of a turn

Every message a user sends goes through the same pipeline. It is a state machine that lives inside one supervising actor.

<pre class="mermaid">
stateDiagram-v2
  direction LR
  [*] --> Idle
  Idle --> Guard: user message
  Guard --> Idle: refused
  Guard --> Classifier: compliant
  Classifier --> Idle: colliding intents, asks the user
  Classifier --> Agent: intent found
  Idle --> Agent: active agent exists
  Agent --> Idle: answer delivered
</pre>

<ol class="steps" markdown="1">
<li markdown="1">
**Idle**
The conversation waits. A press on one of Morgana's own buttons skips the next two stages and lands on that intent's agent.
</li>
<li markdown="1">
**Guard**
A language model reads the message against your policies. A message that fails is answered with a refusal and the turn ends.
</li>
<li markdown="1">
**Classifier**
A language model ranks which intents the message could belong to. Skipped when an agent is already in service. When two intents are too close to call, the user gets a short question instead of a guess.
</li>
<li markdown="1">
**Agent**
The chosen agent runs its tool loop and streams the answer. The wait here is a budget on silence rather than on the turn: every chunk renews it.
</li>
<li markdown="1">
**Back to Idle**
The answer is delivered through whichever channel the user is on. When the agent signals it is done Morgana adds her own closing line.
</li>
</ol>

A message no agent claims is not an error. Morgana answers it herself with her presentation and a button for every intent she can handle.

## What is in the repository

| Unit | What it is | Lives in |
|---|---|---|
| **Morgana.AI** | The core framework library, published on NuGet | `Morgana/Morgana.AI` |
| **Morgana.Contracts** | Zero-dependency wire types shared with every channel | `Morgana/Morgana.Contracts` |
| **Language model plugins** | One per vendor: Anthropic, Azure OpenAI, Google, Ollama and OpenAI | `Morgana/Morgana.AI.LanguageModels` |
| **Morgana.Web** | The ASP.NET Core host that wires it all together | `Morgana/Morgana.Web` |
| **Channels** | [Cauldron]({{ '/channels/cauldron/' | relative_url }}) · [Grimoire]({{ '/channels/grimoire/' | relative_url }}) · [Rune]({{ '/channels/rune/' | relative_url }}) | `Channels/` |
| **Examples** | A sample plugin: four agents and an MCP server | `Examples/` |
| **Alembic** | The [authoring workbench]({{ '/alembic/' | relative_url }}) | `Alembic/` |
| **PromptHarness** | The [live test suite]({{ '/prompt-harness/' | relative_url }}) | `PromptHarness/` |

Each unit has its own solution and reaches the framework by reference. That is what keeps every one of them replaceable by something of your own.

## Three tiers of model

Morgana does not assume one model fits every job. It asks for three, each with its own provider and each declared in full.

| Tier | Meant for | Cost |
|---|---|---|
| `Economy` | Short, mechanical work. Often the framework's own actors and simple lookups | Lowest |
| `Efficiency` | Ordinary domain agents | Middle |
| `Performance` | Agents whose author can argue a real need for deep reasoning | Highest |

There is **no fallback between tiers**. An agent that declares `Performance` is never silently served by a cheaper model. The reasoning behind this is on the [deployment page]({{ '/morgana/configuration/deployment/' | relative_url }}).

## Where to go next

<div class="cards">
<a class="card" href="{{ '/morgana/pillars/' | relative_url }}"><h3>The six pillars</h3><p>Actors, agents, prompts, context, channels and authoring.</p></a>
<a class="card" href="{{ '/morgana/configuration/' | relative_url }}"><h3>Configuration</h3><p>What ships ready and what you provide.</p></a>
<a class="card" href="{{ '/morgana/operations/guard-rails/' | relative_url }}"><h3>Operating Morgana</h3><p>Guard rails, limits, observability and Docker.</p></a>
</div>
