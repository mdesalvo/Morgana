---
title: The six pillars
permalink: /morgana/pillars/
eyebrow: Morgana
lede: Six ideas hold the framework up. Each one answers a question you will meet the first week of building a conversational system.
---

<div class="cards">
<a class="card" href="{{ '/morgana/pillars/actors/' | relative_url }}"><span class="icon">🎭</span><h3>1 · Actors</h3><p>Who does the work and what happens when one of them fails?</p></a>
<a class="card" href="{{ '/morgana/pillars/agents/' | relative_url }}"><span class="icon">🤖</span><h3>2 · Agents</h3><p>How is a domain specialist declared and how does it reach tools and colleagues?</p></a>
<a class="card" href="{{ '/morgana/pillars/prompts/' | relative_url }}"><span class="icon">📝</span><h3>3 · Prompts</h3><p>Where do the instructions live and who wins when two of them disagree?</p></a>
<a class="card" href="{{ '/morgana/pillars/context/' | relative_url }}"><span class="icon">💾</span><h3>4 · Context</h3><p>What does an agent remember and what travels between agents?</p></a>
<a class="card" href="{{ '/morgana/pillars/channels/' | relative_url }}"><span class="icon">📡</span><h3>5 · Channels</h3><p>How does one brain serve a browser, a terminal and a phone-sized screen?</p></a>
<a class="card" href="{{ '/morgana/pillars/authoring/' | relative_url }}"><span class="icon">✨</span><h3>6 · Authoring</h3><p>How does a domain expert who cannot code build an agent?</p></a>
</div>

## How they work together

The pillars are not layers stacked on one another. They are six concerns that meet at every turn.

<pre class="mermaid">
graph TB
  CH["📡 Channels<br/>declare what they can render"] --> AC
  subgraph AC["🎭 Actors"]
    G["Guard"] --> C["Classifier"] --> R["Router"]
  end
  R --> AG["🤖 Agents<br/>declared by attributes"]
  PR["📝 Prompts<br/>morgana.json + agents.json"] -.composed into.-> AG
  CX["💾 Context<br/>private memory + shared registry"] <-.-> AG
  AU["✨ Authoring<br/>Alembic writes the domain"] -.produces.-> PR
  AU -.produces.-> AG
</pre>

A single turn touches all six. The channel states what it can show. The actors decide who handles the message. The agent runs with a prompt composed from two layers and a context assembled from private and shared memory. Authoring is the odd one out: it happens before any conversation does. Its output is the prompt and the agent classes the rest consume.

Read them in order if you are new. Jump to the one you need otherwise.
