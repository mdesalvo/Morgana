---
title: Welcome
permalink: /
description: Morgana is a multi-agent conversational AI framework for .NET. This is its manual.
---

<div class="hero">
  <div class="eyebrow">Conversational AI framework · .NET 10 · Akka.NET · Microsoft.Agents.AI</div>
  <h1>Agents are <em>prose</em>,<br>not code.</h1>
  <p>Morgana orchestrates specialized AI agents that classify, route and resolve what your users ask. You write the domain in plain language. She brings the actors, the guard rails, the memory and every channel.</p>
  <div class="btns">
    <a class="btn primary" href="{{ '/quick-start/' | relative_url }}">Run it in five minutes</a>
    <a class="btn" href="{{ '/morgana/' | relative_url }}">Read the manual</a>
    <a class="btn" href="https://github.com/mdesalvo/Morgana">Source on GitHub</a>
  </div>
</div>

<img class="banner" src="{{ '/assets/images/brand/banner.jpg' | relative_url }}" alt="Morgana">

## A good witch, with books written by you

<figure class="motto">
  <blockquote>A magical witch assistant equipped with an enchanted grimoire powered by AI, yet shaped by you: <strong>your agents, your prompts, your tools</strong>…woven into her spells.</blockquote>
</figure>

This is not a slogan stretched over a framework. It is how Morgana is built. She impersonates a **good witch** who is on the side of the person talking to her. Her grimoire is enchanted by AI but its pages are **written by you and for you**: the agents that answer, the prose that gives each one its voice and the tools that let them act all come from your business. She supplies the craft of magic. You supply what she knows.

Morgana herself speaks through every agent. Billing is a formal witch who keeps the accounts book and Contract is a patient one who reads the scrolls. Each one is recognisably her and each one is specialised for the work in front of her.

## What you get

Morgana is a backend that sits between your users and your language models. A user writes in a browser or in a terminal. A hierarchy of actors checks the message against your policies and works out what it is about. The right agent then answers it, with its own tools and the memory of the conversation so far.

You bring a domain. Morgana brings everything else.

<div class="cards two" markdown="1">
<div class="card" markdown="1">
<span class="icon">🎭</span>

### Resilient by design
One actor hierarchy per conversation. Guard, classifier and router are separate workers that fail and recover on their own.
</div>
<div class="card" markdown="1">
<span class="icon">📝</span>

### Declarative agents
An agent is a block of prose in JSON plus a thin class of tool contracts. Tuning one means editing sentences.
</div>
<div class="card" markdown="1">
<span class="icon">🛡️</span>

### Guarded on every door
User messages, tool results and partner questions are inspected before an agent reads them. Every guard fails closed.
</div>
<div class="card" markdown="1">
<span class="icon">📡</span>

### Every surface
A browser widget and two terminals ship ready. Morgana adapts each answer to what the channel can actually render.
</div>
</div>

## The ecosystem

Six pieces make up the project. One is the framework and the other five orbit it.

<div class="cards">
<a class="card" href="{{ '/morgana/' | relative_url }}">
<span class="icon">🔮</span>
<h3>Morgana</h3>
<p>The framework and its backend. Actors, agents, prompts, context and the rules that keep them honest.</p>
<span class="tag">The core</span>
</a>
<a class="card" href="{{ '/channels/cauldron/' | relative_url }}">
<span class="icon">🌐</span>
<h3>Cauldron</h3>
<p>The browser channel. Streaming, rich cards, quick replies and an embeddable chat widget.</p>
<span class="tag">Channel · rich web</span>
</a>
<a class="card" href="{{ '/channels/grimoire/' | relative_url }}">
<span class="icon">📟</span>
<h3>Grimoire</h3>
<p>The full Morgana experience inside a terminal. Markdown, bordered cards and arrow-key replies.</p>
<span class="tag">Channel · rich terminal</span>
</a>
<a class="card" href="{{ '/channels/rune/' | relative_url }}">
<span class="icon">📜</span>
<h3>Rune</h3>
<p>The deliberately poor terminal. Plain text and 500 characters, so degradation is tested on every turn.</p>
<span class="tag">Channel · poor terminal</span>
</a>
<a class="card" href="{{ '/alembic/' | relative_url }}">
<span class="icon">⚗️</span>
<h3>Alembic</h3>
<p>The authoring workbench. An AI-led interview distils a domain expert's knowledge into a buildable plugin.</p>
<span class="tag">Authoring</span>
</a>
<a class="card" href="{{ '/prompt-harness/' | relative_url }}">
<span class="icon">🧪</span>
<h3>PromptHarness</h3>
<p>The live non-regression suite. It measures whether the model still does what the prompt says.</p>
<span class="tag">Quality</span>
</a>
</div>

<pre class="mermaid">
graph LR
  EXP["Domain expert"] -->|interview| ALE["Alembic"]
  ALE -->|"agents.json + C# plugin"| MOR
  DEV["Developer"] -->|"plugin DLL"| MOR
  subgraph MOR["Morgana"]
    direction TB
    ACT["Actors"] --> AGT["Agents"]
  end
  USR["User"] --> CH["Cauldron · Grimoire · Rune"]
  CH <-->|"REST + SignalR or webhook"| MOR
  MOR <-->|prompts| LLM["Language models"]
  MOR <-.->|tools| MCP["MCP servers"]
  MOR <-.->|A2A| PEER["Partner Morgana"]
  HAR["PromptHarness"] -.->|measures| MOR
</pre>

## Where to start

<div class="cards two">
<a class="card" href="{{ '/quick-start/' | relative_url }}">
<h3>I want to see it working</h3>
<p>Start the stack with Docker and talk to the example shop in your browser or terminal.</p>
</a>
<a class="card" href="{{ '/morgana/pillars/' | relative_url }}">
<h3>I want to understand it</h3>
<p>The six pillars explain what Morgana is made of and why each part is shaped the way it is.</p>
</a>
<a class="card" href="{{ '/morgana/configuration/' | relative_url }}">
<h3>I am deploying it</h3>
<p>What ships configured and what only you can decide: keys, models, limits, partners and channels.</p>
</a>
<a class="card" href="{{ '/morgana/configuration/domain/' | relative_url }}">
<h3>I am building my domain</h3>
<p>Intents, agent prose, tools, MCP servers and colleagues. By hand or through Alembic.</p>
</a>
</div>
