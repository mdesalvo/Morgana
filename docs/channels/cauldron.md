---
title: Cauldron
permalink: /channels/cauldron/
eyebrow: Channel · rich web
lede: Morgana's home. A Blazor Server chat with streaming answers and quick replies and rich cards and animated feedback, plus a widget you can drop into any page.
---

<div class="pills"><span class="pill">Blazor Server</span><span class="pill">SignalR delivery</span><span class="pill">Port 5002</span><span class="pill ok">All capabilities on</span></div>

## A tour

<div class="shots">
{% include shot.html src="cauldron/loader.jpg" caption="Landing: a random spell while Morgana warms up" %}
{% include shot.html src="cauldron/welcome.jpg" caption="Welcome: Morgana introduces herself with a button for each intent" %}
{% include shot.html src="cauldron/agent-landing.jpg" caption="Pressing a button lands on that agent. The header shows who speaks" %}
{% include shot.html src="cauldron/invoices.jpg" caption="Quick replies carry the next likely step. The composer waits while they are on screen" %}
{% include shot.html src="cauldron/invoice-card.jpg" caption="A rich card: structured data instead of a wall of text" %}
{% include shot.html src="cauldron/order.jpg" caption="A dispositive action asks for confirmation through the same card and button vocabulary" %}
{% include shot.html src="cauldron/gardening.jpg" caption="An agent whose tools come from an MCP server answers like any other" %}
</div>

The header changes from **Morgana** to **Morgana (Billing)** when a specialist takes the conversation. A pink accent marks a specialised agent. The conversation survives a refresh because the page rebuilds itself from Morgana's record.

## Features

- **Streaming with a typewriter.** The first chunk replaces the typing indicator and the final text is overwritten with the server's authoritative version in case the adapter rewrote it
- **Quick replies** that disable the composer so the user is never in doubt what is expected
- **Rich cards** built from a small set of polymorphic components: text blocks, key and value rows, lists, sections, grids, badges, dividers and images
- **The dust gauge** showing how much of the conversation's budget is left
- **Persistence across sessions.** Close the tab and come back to the same conversation
- **Trusted images.** A card may load images only from hosts you list in `TrustedImageDomains`

## The widget

You do not need to send people to Cauldron's own page. It publishes a launcher that any website can host, whatever built it.

```html
<script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>
```

There are no parameters. The loader reads its own `src` to learn which Cauldron to open, so a copied snippet points back at the deployment it came from.

<div class="shots">
{% include shot.html src="cauldron/widget.jpg" caption="The widget: a floating pill that opens the real Cauldron chat" %}
</div>

- **Closed** it is a floating pill carrying Morgana's animated face
- **Open** it is a sandboxed `<iframe>` running the **real** chat: streaming, cards, quick replies and dust gauge included
- **Isolation.** A closed shadow root keeps the two stylesheets apart and the iframe keeps the conversation on Cauldron's own origin, unreadable from the host page
- **Framing is closed by default.** A site may host the widget only once its origin is in `Cauldron:Widget:AllowedEmbedOrigins`, which is emitted as a CSP `frame-ancestors` directive

```bash
Cauldron__Widget__AllowedEmbedOrigins__0=https://www.example.com
Cauldron__Widget__AllowedEmbedOrigins__1=https://shop.example.com
```

## Configure it

| Key | Purpose |
|---|---|
| `Cauldron:MorganaURL` | The backend |
| `Cauldron:Authentication:SymmetricKey` | Must match the `cauldron` entry in Morgana's `Issuers[]` |
| `Cauldron:StreamingResponse:*` | Typewriter speed |
| `Cauldron:LandingMessages` | The spells shown while loading. Write your own |
| `Cauldron:TrustedImageDomains` | Hosts a card may load images from |
| `Cauldron:Widget:AllowedEmbedOrigins` | Sites that may frame the widget |

The full list is on the [reference page]({{ '/morgana/configuration/reference/' | relative_url }}).

## Run it

```bash
docker compose up                       # with Morgana, on http://localhost:5002
cd Channels/Cauldron && dotnet run      # from source, https://localhost:5002
```
