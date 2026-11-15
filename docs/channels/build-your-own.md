---
title: Build your own channel
permalink: /channels/build-your-own/
eyebrow: Channels
lede: A channel is a small client that speaks a documented contract. Everything you need is in one dependency-free package.
---

## The contract package

`Morgana.Contracts` is a zero-dependency library of the wire types. Cauldron, Grimoire and Rune consume the same types Morgana's controller returns, so there is no mirror to keep in step.

| Type | For |
|---|---|
| `StartConversationRequest` · `ChannelMetadata` | Opening a conversation and announcing the channel |
| `ChannelCoordinates` · `ChannelCapabilities` | Who you are and what you can render |
| `SendMessageRequest` | A user's message |
| `ChannelMessage` · `MorganaChatMessage` | What comes back |
| `StreamChunkRequest` | One piece of a streamed answer |
| `QuickReply` · `RichCard` | Buttons and the polymorphic card |
| `CommandDescriptor` · `ExecuteCommandRequest` · `CommandProgress` | The command catalogue and a run |

## The lifecycle

<pre class="mermaid">
sequenceDiagram
  participant C as Your channel
  participant M as Morgana
  C->>M: POST conversation/start with ChannelMetadata
  M-->>C: conversation id
  C->>M: POST conversation/{id}/message
  M-->>C: answer by signalr or webhook
  C->>M: GET conversation/{id}/history
  Note over C,M: after a reconnect or a refresh
</pre>

| Endpoint | Method | Purpose |
|---|---|---|
| `conversation/start` | POST | Announces the channel and creates the conversation |
| `conversation/{id}/message` | POST | A user's message |
| `conversation/{id}/history` | GET | The record, for catch-up |
| `conversation/{id}/resume` | POST | Read-only: the active agent and the dust level, to redraw a returning client |
| `conversation/{id}/end` | POST | Stops the conversation's actors |
| `conversation/{id}/command` | POST | Runs a command |
| `commands` | GET | The command catalogue |
| `health` | GET | Liveness |

Every endpoint except `health` authenticates with a Bearer JWT and fails closed.

## Choose a delivery mode

<div class="split" markdown="1">
<div markdown="1">

### `signalr`
Connect to `/morganaHub`. Answers and chunks are pushed to your group. If nobody is in the group the push is lost without a trace, so **catch up from the history** when the client returns.
</div>
<div markdown="1">

### `webhook`
Give a `CallbackUrl` in the coordinates. Morgana POSTs messages and stream chunks to it. A failed POST is seen by Morgana, which retries for about half a minute.
</div>
</div>

## Declare yourself honestly

```csharp
ChannelMetadata metadata = new ChannelMetadata
{
    Coordinates = new ChannelCoordinates
    {
        ChannelName = "slack",
        DeliveryMode = "webhook",
        CallbackUrl = "https://bot.example.com/morgana-hook"
    },
    Capabilities = new ChannelCapabilities(
        SupportsRichCards: false,
        SupportsQuickReplies: true,
        SupportsStreaming: false,
        SupportsMarkdown: true,
        MaxMessageLength: 3000)
};
```

Morgana trims and lowercases the channel name and turns your capabilities into what it will really send. A channel whose limit is too short for rich features loses them. One whose messages must be adapted loses streaming. Declaring less than you can render costs you richness. Declaring more costs you broken screens.

## Authenticate

1. Pick an issuer name for your channel and generate a key of at least 256 bits
2. Add `{ "Name": "<issuer>", "SymmetricKey": "<key>" }` to `Morgana:Authentication:Issuers[]`
3. Sign a short-lived token for each request: HMAC-SHA256, `iss` your issuer, `aud` `morgana.ai`

Morgana reads the `iss`, finds that issuer's entry and validates with **its** key. A key buys the conversation API and nothing under `/a2a`.

## Be a pure presentation layer

Resist the temptation to put logic in the channel. It renders what it is given, keeps the conversation id and a way back to the history and nothing else. The reference channels are the proof that this is enough.
