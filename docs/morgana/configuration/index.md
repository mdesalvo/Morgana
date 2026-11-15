---
title: Two sides of one system
permalink: /morgana/configuration/
eyebrow: Configuration
lede: Morgana is turnkey where a decision has one good answer and yours where only you know the answer. This section separates the two so you know exactly what is left to do.
---

## The split

Everything configurable falls into one of three owners.

<pre class="mermaid">
graph TB
  subgraph box["📦 In the box · Morgana decides"]
    F1["Actors and the turn pipeline"]
    F2["Global policies and framework prompts"]
    F3["Guard rails, limits and defaults"]
    F4["Persistence · auth · tracing"]
    F5["Three reference channels"]
  end
  subgraph ops["🔑 On your side · deployment"]
    O1["Language models per tier"]
    O2["Secrets and channel keys"]
    O3["Limits and messages tuned to you"]
    O4["Partners · public URL · exporters"]
  end
  subgraph dom["🧙 On your side · your domain"]
    D1["Intents and agent prose"]
    D2["Tool contracts · MCP servers"]
    D3["Colleagues and territories"]
  end
  box --> ops --> dom
</pre>

<div class="cards">
<a class="card" href="{{ '/morgana/configuration/in-the-box/' | relative_url }}"><span class="icon">📦</span><h3>In the box</h3><p>What works with no configuration and the defaults it works with.</p><span class="tag">Morgana decides</span></a>
<a class="card" href="{{ '/morgana/configuration/deployment/' | relative_url }}"><span class="icon">🔑</span><h3>Your deployment</h3><p>Models and secrets and keys and limits. Everything an operator provides.</p><span class="tag">Operator decides</span></a>
<a class="card" href="{{ '/morgana/configuration/domain/' | relative_url }}"><span class="icon">🧙</span><h3>Your domain</h3><p>The plugin that makes Morgana an expert in your business.</p><span class="tag">Developer decides</span></a>
</div>

## Who does what

| Concern | Morgana ships it | You provide |
|---|---|---|
| Orchestration of a turn | ✅ Actors, state machine, streaming | |
| Behavioural rules for every agent | ✅ Global policies P0 to P8 | |
| Moderation of messages and tool results | ✅ Three guards, on by default | Switch them off or tune the tier they run on |
| Language models | ✅ Five provider plugins and a price catalogue | **A provider and a model for each tier and the keys** |
| Encryption at rest | ✅ AES-256 SQLite | **The key** |
| Who may call the backend | ✅ JWT validation per issuer | **One issuer and key per channel** |
| Rate and budget limits | ✅ Sensible defaults | Tune numbers and wording |
| Browser and terminal clients | ✅ Cauldron, Grimoire and Rune | Brand them or build your own |
| Tracing | ✅ Spans and metrics | **Where they go** |
| What the agents know and do | | **`agents.json` and the plugin** |
| Talking to other installations | ✅ The protocol and the gates | **Partners, keys and ceilings** |

## The shortest path to a conversation

If you only want the example domain running you need exactly **three things**. Everything else already has a default.

<ol class="steps" markdown="1">
<li markdown="1">
**Two secrets**
`ENCRYPTIONKEY` and `JWT_SYMMETRIC_KEY`, each from `openssl rand -base64 32`.
</li>
<li markdown="1">
**Three model tiers**
A provider and a model id for `Economy`, `Efficiency` and `Performance`, plus an API key where the provider needs one.
</li>
<li markdown="1">
**A tier for Morgana's own actors**
`ACTORSYSTEM_TIER`, normally `Economy`.
</li>
</ol>

Morgana refuses to start if any of these is still a placeholder. That is deliberate: a deployment shipped with the template's values still in place is the failure it exists to prevent.

## How configuration is read

Morgana and every unit of the repository use the standard ASP.NET hierarchy. Later sources win.

1. `appsettings.json`
2. Environment variables, with `__` for nesting: `Morgana__ActorSystem__Tier`
3. User Secrets, for development
4. In Docker, the `environment:` block of `docker-compose.yml`

Sensitive values carry the placeholder `_SECURE_OVERRIDE_` and functional ones that must be chosen carry `_FUNCTIONAL_OVERRIDE_`. Real values arrive through secrets or the environment and never through a commit.
