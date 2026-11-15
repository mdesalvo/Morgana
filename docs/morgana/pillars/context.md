---
title: "4 · Context"
permalink: /morgana/pillars/context/
eyebrow: Pillar 4 of 6
lede: Private by default and shared where it matters. Agents keep their own memory and exchange only what you explicitly mark as travelling.
---

## Private by default

Every agent keeps its own isolated context: its messages, its variables and its conversation state. No other agent can read or write it. That is what lets a dozen specialists work side by side without stepping on each other.

## Shared when marked

Some facts must travel. A customer code given to Billing should not be asked again when Contract takes over. A tool parameter declared with the `SharedContext` scope writes its value into a conversation-scoped **shared registry** and every agent merges that registry at the start of each turn.

<pre class="mermaid">
sequenceDiagram
  participant U as User
  participant B as Billing
  participant R as Shared registry
  participant C as Contract
  U->>B: show my invoices
  B->>U: what is your customer code?
  U->>B: P994E
  B->>R: customerCode = P994E
  Note over B,R: first write wins
  U->>C: and my care plan?
  C->>R: merge at start of turn
  R-->>C: customerCode = P994E
  C->>U: answers without asking again
</pre>

The registry is **first-write-wins**. Once a value is set it is not overwritten by a later agent that guessed differently. The user sees one coherent conversation even when several specialists took turns behind it.

## Persistence

Each conversation lives in its own SQLite file, encrypted at rest with AES-256.

| Table | Holds |
|---|---|
| `morgana` | One row per participant: each agent's session and Morgana's own messages |
| `shared_context` | The registry |
| `channel_metadata` | What the channel announced at the handshake |
| `rate_limit_log` | The sliding windows of the [rate limiter]({{ '/morgana/operations/guard-rails/' | relative_url }}) |
| `dust_budget` · `dust_usage_log` | The lifetime budget and what each charge cost |

A user's phrase belongs to Morgana when no agent is active and to the active agent otherwise. An answer belongs to whoever wrote it. When a client returns after a restart or a refresh, history is rebuilt by decrypting each row and merging them in time order, with each answer's quick replies and card restored beside its text.

## Long conversations

A conversation that grows long is **summarised** by Morgana's own history reducer once it crosses a threshold. Unlike a generic summariser it keeps tool calls and results in view, so an agent that did real work never summarises itself as having done nothing. The prompt insists that identifiers and amounts survive verbatim, because a later turn cannot ask a tool for a value it was never given.

The user can also fold the record by hand with the `/compact` command, which the terminal channels list in their command palette.

## Your levers

| Lever | Where |
|---|---|
| Which parameters are shared | `[ToolParameter(Records.ToolScope.SharedContext)]` on the tool |
| Where databases live and the key that encrypts them | `ConversationPersistence:StoragePath` and `:EncryptionKey` |
| When history is summarised and how | `HistoryReducer:*` |
