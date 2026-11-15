---
title: "1 · Actors"
permalink: /morgana/pillars/actors/
eyebrow: Pillar 1 of 6
lede: Resilient orchestration through Akka.NET. Each conversation is a small company of specialists that talk by message and never share state.
---

## Why actors

A conversation is a long-lived, stateful, concurrent thing. Messages arrive while an earlier answer is still streaming. A model call can take seconds or fail. A guard must run before a classifier and a classifier before a router. Threads and locks make this fragile. The **actor model** makes it natural: every worker owns its state, receives messages one at a time and never touches anyone else's memory.

Morgana builds on [Akka.NET](https://getakka.net) for this. The practical gains are isolation between conversations and recovery that does not need a human.

## The company of one conversation

<pre class="mermaid">
graph TD
  CM["ConversationManager<br/>entry point and lifecycle"] --> SV["ConversationSupervisor<br/>the turn's state machine"]
  SV --> G["Guard<br/>moderation"]
  SV --> C["Classifier<br/>intent ranking"]
  SV --> R["Router<br/>intent to agent"]
  R --> A1["Agent"]
  R --> A2["Agent"]
</pre>

| Actor | Responsibility |
|---|---|
| **ConversationManager** | The stable entry point. Owns the lifecycle of one user session and the channel metadata announced at the start |
| **ConversationSupervisor** | Orchestrates the whole flow as a state machine and coordinates its children |
| **Guard** | Checks every user message against your business policies and brand guidelines |
| **Classifier** | Reads the message and ranks the intents it could belong to, falling back to `other` |
| **Router** | Sends the request to the agent that handles the winning intent and runs the domain agents |

Actors are addressed as `/user/{suffix}-{conversationId}` and an agent identifier is `{agent_name}-{conversation_id}`. One conversation never shares an actor with another.

## One hop at a time

<pre class="mermaid">
sequenceDiagram
  participant U as User
  participant CH as Channel
  participant CM as Manager
  participant SV as Supervisor
  participant G as Guard
  participant C as Classifier
  participant R as Router
  participant A as Agent
  participant L as Language model
  U->>CH: message
  CH->>CM: REST
  CM->>SV: UserMessage
  SV->>G: check
  G->>L: policy prompt
  L-->>G: compliant
  SV->>C: classify
  C->>L: ranking prompt
  L-->>C: intents with confidence
  SV->>R: route
  R->>A: activate
  A->>L: instructions and tools
  A-->>CH: streamed chunks
</pre>

## Design rules that matter

- **Messages are immutable records.** Every actor message lives in one file, `Records.cs`. A message cannot be changed after it is sent, so there is nothing to race on.
- **`Tell`, never `Ask`.** Streaming answers arrive in pieces and an `Ask` waits for one reply. Actors tell each other and the supervisor changes state with `Become()`.
- **The wait is on silence.** Each chunk of a streamed answer renews the supervisor's patience. An agent that consults a colleague keeps signalling that it is still working, so a long consultation never reads as a dead agent.
- **A conversation survives a restart.** It is served from its record, not from whichever process saw it first. The active agent is read back at the next turn and a client that only reconnected carries on where it was.

## What this buys you

<div class="split" markdown="1">
<div markdown="1">

### You get
- Failure isolation: a stuck conversation does not slow another
- A pipeline you can read top to bottom as a state machine
- Spans for every actor in your tracing backend
</div>
<div markdown="1">

### You decide
- Which tier the actors run on (`ActorSystem:Tier`)
- The timeout and which guards are on
- How close two intents may rank before the user is asked
</div>
</div>

The settings are listed on the [configuration reference]({{ '/morgana/configuration/reference/' | relative_url }}) and the guards are described under [guard rails]({{ '/morgana/operations/guard-rails/' | relative_url }}).
