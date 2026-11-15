---
title: "5 · Channels"
permalink: /morgana/pillars/channels/
eyebrow: Pillar 5 of 6
lede: One brain and every surface. Rich where the screen can take it and plain where it must be, with nothing lost in translation.
---

## Morgana adapts. Channels never have to keep up.

A channel is the transport and client pair that shows Morgana's answers to a person. Morgana never asks a channel to cope with more than it can. At the start of a conversation the channel **declares what it can express** and from then on every outbound message is shaped to that declaration.

```csharp
Capabilities = new ChannelCapabilities(
    SupportsRichCards:    true,
    SupportsQuickReplies: true,
    SupportsStreaming:    true,
    SupportsMarkdown:     true,
    MaxMessageLength:     null);   // null means no limit
```

A rich card, a quick reply or a streamed chunk is offered only where the channel says it can render it. Everywhere else it is degraded to plain text. The same conversation and agents and policies run on every surface. Only the presentation changes.

## The reference matrix

Three channels ship with Morgana. They were chosen to fill a small matrix so the multi-channel design is demonstrable without extra work.

| | Browser (HTML) | Terminal (TTY) |
|---|---|---|
| **Full capabilities** | [Cauldron]({{ '/channels/cauldron/' | relative_url }}) | [Grimoire]({{ '/channels/grimoire/' | relative_url }}) |
| **Poor capabilities** | | [Rune]({{ '/channels/rune/' | relative_url }}) |

Rune is poor on purpose. It exists so the degradation path is exercised on **every turn** and cannot quietly rot. Neither rich channel contains a line of agent logic: they are pure presentation.

<div class="shots">
{% include shot.html src="cauldron/welcome.jpg" caption="Cauldron: Morgana greets the user with a button for each intent" %}
{% include shot.html src="grimoire/welcome.jpg" caption="Grimoire: the same turn rendered in a terminal" wide=true %}
{% include shot.html src="rune/welcome.jpg" caption="Rune: the same turn in plain text. No buttons, so the intents are named in the sentence" wide=true %}
</div>

## The handshake

When a client starts a conversation it announces a `ChannelMetadata` made of two parts.

| Part | Carries |
|---|---|
| `ChannelCoordinates` | The channel's name and its **delivery mode**, plus a callback URL for webhooks |
| `ChannelCapabilities` | The capability profile above |

Morgana rejects a conversation with a missing or unserved delivery mode. Otherwise it **settles** the metadata before answering: names are trimmed and lowercased and capabilities become what Morgana will actually send. A channel too short for rich features loses them. One whose messages will need adapting loses streaming. The result is persisted and every reader takes it as is.

## Delivery modes

| Mode | How an answer reaches the client | Failed delivery |
|---|---|---|
| `signalr` | Pushed over a SignalR hub | Lost without a trace on Morgana's side, so the client catches up from history when it returns |
| `webhook` | POSTed to the channel's callback URL | Seen by Morgana, which retries for about half a minute |

Both modes date a reply identically, so a catch-up can tell a missed reply from one already shown.

## Adaptation

<pre class="mermaid">
graph LR
  A["Outbound message"] --> B{"Fits the<br/>channel budget?"}
  B -- yes --> D["Delivered as is"]
  B -- no --> C["LLM-guided rewrite"]
  C -- fails --> T["Template fallback"]
  C --> D
  T --> D
</pre>

The adapter first short-circuits when the message already fits. Otherwise a model rewrites it to the budget. If that fails a Markdig template takes over. The adapter never throws.

## Authentication per channel

Each channel signs its own tokens with its own issuer name and key and Morgana validates each one against **its** key. Leaking one channel's key does not compromise the others. The details are under [build your own channel]({{ '/channels/build-your-own/' | relative_url }}).
