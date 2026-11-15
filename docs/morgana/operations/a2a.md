---
title: Agent to agent
permalink: /morgana/operations/a2a/
eyebrow: Operating
lede: Agents consult colleagues over the A2A protocol, whether the colleague sits in the same installation or in another Morgana. This page is about turning it on and about trust.
---

## Three shapes

How much you configure depends on what you want. Pick the shape that matches.

<div class="cards" markdown="1">
<div class="card" markdown="1">

### 1 · Off
Nothing is published. No route and no card and not one extra prompt token. The classic Morgana.

```json
"AgentToAgent": { "Enabled": false }
```
</div>
<div class="card" markdown="1">

### 2 · On, no partners
Agents of this installation consult each other. Each colleague is named by `[ConsultsAgent]` in code. Nothing outside is involved.

```json
"AgentToAgent": {
  "Enabled": true,
  "MaxRoundsPerTurn": 4,
  "Partners": []
}
```
</div>
<div class="card" markdown="1">

### 3 · On, with partners
As shape 2 and one entry per trusted Morgana. Each opens up to two directions.

See below.
</div>
</div>

## One consultation, step by step

<pre class="mermaid">
sequenceDiagram
  participant B as Billing (asking)
  participant H as Your Morgana
  participant X as Partner Morgana
  participant S as Shipping (answering)
  B->>H: model calls consult_acme_shipping
  H->>X: GET agent card
  Note over H,X: PeerGuard-style inspection of the card before anything is signed
  H->>X: JSON-RPC question signed with the shared key
  X->>X: partner gate: key proven and agent admitted
  X->>X: admission: conversations per hour
  X->>X: PeerGuard reads the question
  X->>S: ask on an ephemeral session
  S-->>X: answer
  X-->>H: answer
  H-->>B: result of the call
  Note over B: the consult call and its result are stripped from history
</pre>

Inside one installation the same steps run without the network hop. The traffic is signed under the reserved issuer `morgana` with a key coined at startup.

## What to know first

- **Publication is whole or nothing.** With A2A on, *every* agent is published whether or not a sibling consults it. What an installation offers is what it can answer
- **The gate is the partner, never the topology.** To keep a caller from an agent you limit that partner's `InboundPolicy`. You never decline to publish
- **A caller is a channel or a partner, never both.** A name in both lists stops startup
- **Nothing leaves a trace.** The answering side uses an ephemeral session and the asking side strips the consultation from its own history once read
- **Nothing is served on credit.** A partner's request skips the user's guard and rate limit, so `IPeerAdmissionService` bounds how many conversations a partner may open and the PeerGuard reads what it asks

## Declaring a partner

```json
"AgentToAgent": {
  "Enabled": true,
  "PublicUrl": "https://morgana.greenhouse.example",
  "Partners": [
    {
      "Name": "acme",
      "Url": "https://acme.example.com",
      "SymmetricKey": "...",
      "Enabled": true,
      "OutboundPolicy": { "Enabled": true, "Issuer": "greenhouse" },
      "InboundPolicy": {
        "Enabled": true,
        "OnAgents": ["billing", "inventory"],
        "RateLimiting": {
          "Enabled": true,
          "MaxConversationsPerHour": 100,
          "ErrorMessagePerHour": "This agent cannot take on further conversations right now."
        }
      }
    }
  ]
}
```

| Field | Means |
|---|---|
| `Name` | What `[ConsultsAgent]` writes and the issuer the partner's calls arrive under. Never a hostname. `morgana` is reserved |
| `Url` | Where the partner answers |
| `SymmetricKey` | One shared key. Calls out are signed with it and calls in are proven against it |
| `OutboundPolicy.Issuer` | The name the **partner** knows you by. Nothing can be signed without it |
| `InboundPolicy.OnAgents` | The agents that partner reaches. Leave it out to admit all |
| `InboundPolicy.RateLimiting` | **Required.** Startup refuses an admitted partner with none. To grant a free hand write `"Enabled": false` |

The partner's `Enabled` parks the whole relationship without deleting its description. Each direction has its own switch. An entry with neither open is refused.

### In code

```csharp
[ConsultsAgent("inventory")]            // here      → consult_inventory
[ConsultsAgent("shipping", "acme")]     // at acme   → consult_acme_shipping
public class BillingAgent : MorganaAgent { /* ... */ }
```

## Your address

Almost nothing describes this installation. There is no key to configure for the ring of your own agents because it is coined at every start. There is no address either, since the card names whatever Kestrel bound. The one exception is `PublicUrl`. Declare it when you sit behind an ingress or a reverse proxy or a published container port, where the binding is not the place a peer knocks.

> Every interface a card advertises must share the origin the card was fetched from. The card is read before anything is signed, so it may only send you where you already were.
{: .callout}

## Cards and territory

Each agent publishes a card at `/a2a/{intent}` that carries its `Territory`. That sentence is what colleagues read to decide whether a question belongs to it, which is why Alembic asks for it explicitly. The card endpoint stays open by design and the JSON-RPC endpoints sit behind the partner gate.
