---
title: "📡 Channels"
permalink: /channels/
eyebrow: Pillar 5 of 6
tagline: "One brain, every surface: rich where it can be, plain where it must be"
lede: A channel is a mirror through which a customer sees me. Three mirrors come with me and the same conversation shines in all of them.
---

## I adapt to the mirror

A web page can show buttons, cards and an answer that appears word by word. A text message or a plain terminal cannot. My witches never have to care about the difference.

When a mirror opens a conversation, it tells me what it can show: cards or not, buttons or not, live writing or not and the longest message it accepts. From then on I shape every answer to fit. What a mirror cannot show I turn into plain words, so nothing is lost and nothing breaks. When an answer is too long, I rewrite it shorter.

My witches write one answer. Every mirror gets the version it can show.

<div class="shots three">
{% include shot.html src="cauldron/invoice-card.jpg" caption="Cauldron: the invoice as a card with buttons" %}
{% include shot.html src="grimoire/invoices.jpg" caption="Grimoire: the same card drawn in a terminal" %}
{% include shot.html src="rune/turn.jpg" caption="Rune: the same answer in plain words, within 500 characters" %}
</div>

## Cauldron, my mirror in the browser {#cauldron}

Cauldron is my home: a chat page with every enchantment switched on. Answers appear as they are written, buttons offer the likely next step and data arrives as tidy cards. A gauge in the header shows how much [magic dust]({{ '/dust/' | relative_url }}) the conversation has left. Closing the tab loses nothing: coming back opens the same conversation.

<div class="shots two">
{% include shot.html src="cauldron/loader.jpg" caption="While I wake up, Cauldron shows one of my little spells" %}
{% include shot.html src="cauldron/invoices.jpg" caption="Buttons offer the next step. The text box waits while they are shown" %}
{% include shot.html src="cauldron/order.jpg" caption="An order is confirmed with the same card and buttons" %}
{% include shot.html src="cauldron/agent-landing.jpg" caption="A witch takes the conversation. The header says who is speaking" %}
</div>

### Hang my charm on your website

You need not send customers to Cauldron's own page. Cauldron offers a **charm**, a widget in code: a small floating button that opens my real chat on top of any page. Hanging it takes one line in your site's HTML:

{% include shot.html src="cauldron/widget.jpg" caption="My charm opened on a shop's page: my real chat, cards and dust gauge included" narrow=true %}

```html
<script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>
```

The line needs no parameters: it knows which Cauldron it came from. To keep strangers from borrowing me, Cauldron refuses to appear inside a site you have not named. List each site in `Cauldron:Widget:AllowedEmbedOrigins`:

```bash
Cauldron__Widget__AllowedEmbedOrigins__0=https://www.example.com
```

## Grimoire, my mirror in a terminal {#grimoire}

Grimoire is the whole Cauldron experience inside a terminal window. Cards are drawn as boxes, buttons become a list you walk with the arrow keys and answers appear as they are written.

<div class="shots">
{% include shot.html src="grimoire/welcome.jpg" caption="My welcome. The arrow keys and Enter choose a button" %}
{% include shot.html src="grimoire/palette.jpg" caption="Typing / opens my list of commands" %}
</div>

| Command | Does |
|---|---|
| `/compact` | Summarises the conversation so far, to spare dust. What you see on screen does not change |
| `/export` | Saves the conversation to a file |
| `/new` | Starts a new conversation |
| `/status` | Shows the connection and the state of the conversation |
| `/help` | Explains the keys and the commands |
| `/exit` | Leaves |

## Rune, my humble mirror {#rune}

Rune shows plain text only, at most 500 characters per message. That is on purpose. It proves on every single turn that my adapting works: no buttons, no cards, answers shortened to fit. With no buttons to press, the customer writes whole sentences, such as *"I'd like to see my invoices, my customer code is P994E"*.

{% include shot.html src="rune/welcome.jpg" caption="In Rune my welcome names the subjects in words, since it cannot show buttons" %}

Rune knows the same commands as Grimoire.

## Side by side

| | Cauldron | Grimoire | Rune |
|---|---|---|---|
| Where | Browser | Terminal | Terminal |
| Live writing | ✅ | ✅ | ❌ |
| Cards | ✅ | ✅ as boxes | ❌ |
| Buttons | ✅ | ✅ arrow keys | ❌ |
| Message limit | none | none | 500 characters |
| Address | `localhost:5002` | `localhost:5004` | `localhost:5003` |

## A mirror of your own

Slack, WhatsApp, a kiosk in a shop: any surface can become one of my mirrors. A channel is a small program that does four things:

1. opens a conversation and tells me what it can show
2. proves who it is with a signed token, made with a key I know and valid for 5 minutes, like every token my own mirrors make
3. sends me the customer's messages through my web API
4. shows what I send back, received either live over SignalR or as calls to a web address of its own, which my settings name in advance

Everything it needs is in one small package with no dependencies, `Morgana.Contracts`, the same one my three mirrors use. A mirror knows no magic of its own: it only shows what I send. I admit each mirror with one entry in my settings, described in [Your choices]({{ '/install/#my-mirrors' | relative_url }}).

## The mirrors' own settings

Each mirror has its own small settings file. The ones you are likely to touch:

| Setting | What it is for |
|---|---|
| `…:MorganaURL` | Where I live |
| `…:Authentication:SymmetricKey` | The mirror's key. It must match the key I hold for that mirror |
| `Cauldron:Widget:AllowedEmbedOrigins` | The websites allowed to hang my charm |
| `Cauldron:TrustedImageDomains` | The sites a card may show pictures from |
| `…:LandingMessages` | The little spells shown while I wake up. Write your own |
| `Rune:MaxMessageLength` | Rune's message limit: 500 unless you change it |

Here `…` stands for `Cauldron`, `Grimoire` or `Rune`.
