---
title: Faraway covens
permalink: /partners/
eyebrow: Installing me
lede: Out there live other witches, each a master of a craft of her own. If you let me, my witches can consult them. Let me show you when that is worth it, how you make the introduction and how I keep the door safe.
---

## Three ways to live

<div class="cards three">
<div class="card"><h3>On my own</h3><p>My witches never ask each other anything. Nothing extra runs and not one extra word reaches the AI.</p></div>
<div class="card"><h3>One coven</h3><p>My witches ask their sisters at home. This is how I come and it needs no setup.</p></div>
<div class="card"><h3>Faraway covens</h3><p>Some of my witches consult, or are consulted by, the witches of another Morgana.</p></div>
</div>

The first two are one switch:

```js
"AgentToAgent": { "Enabled": false }      // on my own
"AgentToAgent": { "Enabled": true }       // one coven: how I come
```

## Why consult a faraway witch

Think of my little nursery. It knows plants, not roads. Somewhere else lives a coven of couriers, with a witch who knows every delivery by heart. A customer asks my stock witch *"when will my order arrive?"*. If you let her, she consults the courier witch and answers in one go. Neither coven opens its cupboards to the other: one question and one answer cross between them, nothing more.

In my nursery's code, the faraway sister is named together with her coven:

```csharp
[ConsultsAgent("inventory")]            // a sister at home
[ConsultsAgent("shipping", "acme")]     // a sister in the faraway coven called acme
public class BillingAgent : MorganaAgent { /* ... */ }
```

## Making the introduction

Two covens introduce themselves to each other: each one declares the other. The entry says who the faraway coven is, where it lives, the secret key the two share and what is allowed in each direction:

```json
"AgentToAgent": {
  "Enabled": true,
  "PublicUrl": "https://morgana.nursery.example",
  "Partners": [
    {
      "Name": "acme",
      "Url": "https://morgana.acme.example",
      "SymmetricKey": "...",
      "Enabled": true,
      "OutboundPolicy": { "Enabled": true, "Issuer": "nursery" },
      "InboundPolicy": {
        "Enabled": true,
        "OnAgents": ["billing", "inventory"],
        "RateLimiting": { "Enabled": true, "MaxConversationsPerHour": 60 }
      }
    }
  ]
}
```

| Field | What it is for |
|---|---|
| `Name` | The faraway coven's name, the one you write in `[ConsultsAgent]`. `morgana` is reserved for my own coven |
| `Url` | Where the faraway Morgana lives |
| `SymmetricKey` | The secret only the two covens share. Forge it with `openssl rand -base64 32` |
| `Enabled` | Puts the friendship to sleep without forgetting it |
| `OutboundPolicy` | **My witches consult theirs.** `Issuer` is the name the faraway coven knows *me* by, taken from its own declaration |
| `InboundPolicy` | **Their witches consult mine.** `OnAgents` limits which of my witches they may reach. Leave it out to admit all of them |
| `InboundPolicy:RateLimiting` | How many conversations the faraway coven may open with me in an hour. It is required: write `"Enabled": false` only if you really mean no limit |
| `PublicUrl` | The address faraway covens reach *me* at. Needed only when I sit behind a proxy, a load balancer or a published container port, where my own address is not the one they knock on |

A faraway coven with neither direction open is refused at startup, since the friendship would do nothing.

In Docker the shipped `.env` already holds these lines, commented out: `MORGANA_PUBLIC_URL` and `PARTNER_0_NAME`, `PARTNER_0_URL`, `PARTNER_0_SYMMETRIC_KEY`, `PARTNER_0_OUTBOUND_ISSUER`, `PARTNER_0_MAX_CONVERSATIONS_PER_HOUR`. Uncomment them together with their matching lines in `docker-compose.yml`.

## How I keep the door safe

A faraway witch reaches mine without passing through the guard and the message limit a customer meets, so I watch that door with special care:

- **A secret per coven.** Nobody but that coven can sign its questions. My mirrors' keys never open this door.
- **A ceiling.** A faraway coven may open only so many conversations an hour. When I cannot check the count, I refuse.
- **A guard of its own.** My PeerGuard reads every question before my witch does and refuses anything that tries to steer her.
- **No traces left.** My witch answers on a page that is torn out right afterwards and the asking witch removes the exchange from her notebook once she has read the answer. Neither conversation fills up with the other coven's words.
- **Nobody else sets my voice.** A faraway coven cannot change my language, my tone or my rules. A question that tries is refused.

## How a witch knows whom to consult

Every witch of mine publishes a little **card** that carries her `Territory`, the chapter of her tome written for her sisters. A witch with sisters reads their cards to decide whether a question belongs to one of them. That is why my [alembic]({{ '/alembic/' | relative_url }}) asks for the territory with care.
