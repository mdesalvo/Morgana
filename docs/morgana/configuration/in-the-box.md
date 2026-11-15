---
title: In the box
permalink: /morgana/configuration/in-the-box/
eyebrow: Configuration · Morgana decides
lede: What works the moment Morgana starts. Each default below is a deliberate answer and you change it only when you have a reason.
---

<div class="pills"><span class="pill ok">Nothing here is required</span><span class="pill ok">Every value overridable</span></div>

## The turn pipeline

The five-stage pipeline of [pillar 1]({{ '/morgana/pillars/actors/' | relative_url }}) works as shipped: guard, classifier, router and agent, with streaming and silence-based timeouts. A request nobody claims is answered by Morgana herself and two intents too close to call produce a disambiguation question.

## The framework prompts

`morgana.json` carries every prompt the framework itself needs.

| Prompt | Job |
|---|---|
| **Morgana** | The global character and the nine global policies P0 to P8 |
| **Classifier** | Ranks intents as JSON and owns the `other` complement no domain declares |
| **UserGuard** | Judges a user's message: compliant or not and why |
| **ToolGuard** | The same judgement on what a tool, an MCP server or a partner's card hands over |
| **PeerGuard** | The same judgement on a question a partner puts to one of your agents |
| **Presentation** | Morgana's welcome and her reintroduction |
| **ChannelAdapter** | The rewrite of a message to a channel's budget |

The three guards share one doctrine of malice verbatim and differ only in the vector they watch.

## Guard rails

All three guards are **on** and all three **fail closed**: what no guard could judge is refused, the user's message included. A deterministic check on tool results sits in front of the model-based one.

## Defaults worth knowing

| Area | Default | Meaning |
|---|---|---|
| Actor timeout | 180 s | A budget on silence. Every chunk renews it |
| Intent collision threshold | 0.10 | Two intents ranked within 10 points make the user choose |
| Tool result length | 32 000 chars | The longest result an agent is handed |
| Streaming | on | Answers stream where the channel can draw them |
| Rich features minimum | 1 000 chars | A channel whose message limit is shorter loses rich cards and quick replies at the handshake |
| Rate limit | 5 per minute · 30 per hour · 80 per day | Per conversation, sliding windows. **Fails open** |
| Dust budget | 100 per conversation | Warnings at 70 % and 90 %. A lockout message at the end. **Fails open** |
| History reducer | Summarise past 12 messages down to 8 | Keeps tool calls and identifiers |
| Model reliability | 60 s timeout · 2 retries | Laid under every tier's connection |
| Agent to agent | on · 4 rounds per turn | Agents of this installation consult each other |
| Tracing | on · console and OTLP | Spans and metrics out of the box |
| Plugin directory | `plugins/` | Scanned at startup |
| Token audience | `morgana.ai` | What a JWT must name |

## Language models and prices

Morgana ships five provider plugins: **Anthropic**, **AzureOpenAI**, **Google**, **Ollama** and **OpenAI**. Each carries a catalogue of its models with the output ceiling of each and its price, which the framework converts into **dust**, Morgana's currency of AI consumption. A tier inherits the price of the model it names. A model absent from the catalogue declares its own prices. See [Dust, models and pricing]({{ '/morgana/configuration/dust-and-models/' | relative_url }}).

## Persistence and security

- **Encrypted SQLite**, one file per conversation, with the schema versioned in the database itself
- **JWT, per-issuer trust**: each channel signs with its own key and Morgana validates each one against that channel's entry
- **An internal ring**: agents of the same installation consult each other under a key coined at every start and configured nowhere
- **Nine startup checks**, all fatal. See [startup validation]({{ '/morgana/operations/startup-validation/' | relative_url }})

## Commands

The framework publishes `/compact`, which folds the active agent's history and reports progress. Commands are registered only in the host. A plugin cannot publish one, by design.

## Three channels and an example

- [Cauldron]({{ '/channels/cauldron/' | relative_url }}), [Grimoire]({{ '/channels/grimoire/' | relative_url }}) and [Rune]({{ '/channels/rune/' | relative_url }}), with the handshake and authentication wired
- The [Greenhouse example]({{ '/examples/' | relative_url }}): four agents and an MCP server, built into the backend image
- A `docker-compose.yml` that starts the backend and the browser channel and keeps the terminals and Alembic on their own profiles

## What is *not* in the box

A language model account, any secret and any knowledge of your business. Those are on your side.
