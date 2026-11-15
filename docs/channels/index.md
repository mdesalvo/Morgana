---
title: The channel matrix
permalink: /channels/
eyebrow: Channels
lede: A channel is where a person meets Morgana. Three ship with the framework and each shows the same conversation in a different dress.
---

## One conversation and three dresses

<div class="shots">
{% include shot.html src="cauldron/invoice-card.jpg" caption="Cauldron · a browser with streaming and rich cards and buttons" %}
{% include shot.html src="grimoire/invoices.jpg" caption="Grimoire · the same card drawn in a terminal" wide=true %}
{% include shot.html src="rune/turn.jpg" caption="Rune · the same answer in plain prose within 500 characters" wide=true %}
</div>

## Pick the one you need

<div class="cards">
<a class="card" href="{{ '/channels/cauldron/' | relative_url }}"><span class="icon">🌐</span><h3>Cauldron</h3><p>A Blazor Server chat with everything switched on. Also an embeddable widget for any website.</p><span class="tag">HTML · SignalR · full</span></a>
<a class="card" href="{{ '/channels/grimoire/' | relative_url }}"><span class="icon">📟</span><h3>Grimoire</h3><p>The textual Cauldron. Markdown and bordered cards and an arrow-key selector.</p><span class="tag">TTY · webhook · full</span></a>
<a class="card" href="{{ '/channels/rune/' | relative_url }}"><span class="icon">📜</span><h3>Rune</h3><p>Poor on purpose. A working proof that the degradation path holds.</p><span class="tag">TTY · webhook · poor</span></a>
<a class="card" href="{{ '/channels/build-your-own/' | relative_url }}"><span class="icon">🔌</span><h3>Your own</h3><p>Slack and WhatsApp and a kiosk. The handshake and the contracts are all you need.</p><span class="tag">Any transport</span></a>
</div>

## Side by side

| | Cauldron | Grimoire | Rune |
|---|---|---|---|
| Surface | Browser | Terminal | Terminal |
| Delivery mode | `signalr` | `webhook` | `webhook` |
| Streaming | ✅ typewriter | ✅ live pane | ❌ |
| Markdown | ✅ | ✅ rendered to the terminal | ❌ |
| Rich cards | ✅ | ✅ bordered boxes | ❌ |
| Quick replies | ✅ buttons | ✅ arrow-key selector | ❌ |
| Message limit | none | none | 500 characters |
| Backend port | 5002 | 5004 | 5003 |
| Issuer | `cauldron` | `grimoire` | `rune` |
| Exercises | The full experience | Full capabilities in a TTY | The degradation path, every turn |

Because Cauldron and Grimoire declare every capability, Morgana's channel adapter short-circuits for them. Rune is the one that proves the adapter works.

## What every channel does

1. Announces its [capabilities and coordinates]({{ '/morgana/pillars/channels/' | relative_url }}) when a conversation starts
2. Signs a short-lived token under its own issuer for every request
3. Sends the user's message by REST and receives the answer by its delivery mode
4. Draws what it receives and nothing more. It carries no agent logic
