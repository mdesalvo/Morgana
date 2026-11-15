---
title: Guard rails and limits
permalink: /morgana/operations/guard-rails/
eyebrow: Operating
lede: Morgana inspects every door through which her models are engaged and meters every conversation. This page explains what is checked and what happens when a check cannot be made.
---

## Three guards for three doors

A language model reads text that someone else wrote. Whoever wrote it may be trying to steer the model. Morgana puts a guard on each door where outside text comes in.

<pre class="mermaid">
graph LR
  U["User message"] --> UG{"UserGuard"}
  T["Tool result · MCP server offer · partner card"] --> TG{"ToolGuard"}
  P["Partner's question"] --> PG{"PeerGuard"}
  UG --> A["Agent"]
  TG --> A
  PG --> A
</pre>

| Guard | Reads | Switch |
|---|---|---|
| **UserGuard** | A user's message, in the light of Morgana's last answer | `ActorSystem:EnableUserGuardrail` |
| **ToolGuard** | Every tool result and what MCP servers and partners offer: a server's tools at startup and again whenever its listing changes and a partner's card | `ActorSystem:EnableToolGuardrail` |
| **PeerGuard** | A question a partner puts to one of your agents | `ActorSystem:EnablePeerGuardrail` |

A deterministic check on tool results runs before the model-based one and refuses a result that forges the prompt fences. A withheld result is replaced by a fact telling the agent it was quarantined. A source caught trying to steer an agent is closed to **every** agent.

The three guards share one doctrine of malice and differ only in who holds the pen. They protect with judgement. A guard that is petulant about difficult but good-faith topics is as much a defect as one that is lenient.

## Fail closed or fail open

The direction of a failure is a design decision and it differs by door.

| Component | If it cannot decide | Reason |
|---|---|---|
| UserGuard · ToolGuard · PeerGuard | **Refuses** | Letting through what nobody could judge is the attack |
| Partner admission | **Refuses** | A partner reaches an agent with none of the user's protections |
| Rate limit | Allows | A broken ledger should not stop a customer from being served |
| Dust budget | Allows | The same |
| Partner and issuer authentication | **Refuses** | An unproven caller is not a caller |

## Rate limiting

A sliding window per conversation, evaluated per minute and hour and day. The shipped limits are 5, 30 and 80 messages. Each limit has its own refusal text, which you write.

```json
"RateLimiting": {
  "Enabled": true,
  "MaxMessagesPerMinute": 5,
  "ErrorMessagePerMinute": "✋ Whoa there! You're casting spells too quickly."
}
```

## Dust: a budget in the currency of cost

The full treatment, with the equation and the catalogue, is on [Dust, models and pricing]({{ '/morgana/configuration/dust-and-models/' | relative_url }}).


Every model call is priced from the provider's catalogue and converted into **dust**. Each conversation has a lifetime budget (100 by default). At 70 % and 90 % of it the user sees a gentle warning. At the end the conversation is complete and a new one starts fresh.

- Prices live in each provider plugin and can be overridden per tier
- One service owns every dust question. No caller does the arithmetic itself
- Cauldron and Grimoire show the remaining level in their header

## The order of the gates

For a REST message the filters run in a fixed order. Each is cheaper than the next.

<ol class="steps" markdown="1">
<li markdown="1">
**Authentication**
A valid token from an admitted channel.
</li>
<li markdown="1">
**Known conversation**
Otherwise 404.
</li>
<li markdown="1">
**Rate limit**
Otherwise 429 with your message.
</li>
<li markdown="1">
**Dust budget**
Otherwise the lockout message.
</li>
<li markdown="1">
**The turn**
The guard, the classifier and the agent.
</li>
</ol>
