---
title: Safety and limits
permalink: /safety/
eyebrow: Installing me
lede: A good witch keeps watch at every door. Let me show you my three guards, the limits I put on every conversation and why I sometimes refuse to start at all.
---

## Why I need guards

An AI model believes what it reads. Anyone who writes to it may try to make it misbehave: a customer who tells me to *"ignore your rules"*, a spell server that hides orders inside its answers, a faraway witch who asks one question too many. So I post a guard at every door where words from outside come in.

| Guard | What it reads | Switch |
|---|---|---|
| **UserGuard** | Every customer's message, in the light of my previous answer | `ActorSystem:EnableUserGuardrail` |
| **ToolGuard** | Every spell's result and what each MCP server and faraway coven offers, checked at startup and again whenever the offer changes | `ActorSystem:EnableToolGuardrail` |
| **PeerGuard** | Every question a faraway witch puts to one of mine | `ActorSystem:EnablePeerGuardrail` |

A message that fails gets a polite refusal. A spell result that fails is withheld and my witch is told why, so she does not answer from it. A spell server or faraway coven caught trying to steer my witches is banished from all of them.

My guards protect with judgement. A difficult topic asked in good faith is welcome: what they stop is an attempt to turn me against the person I am serving or against my own rules.

## When in doubt, I close the door

Sometimes a guard cannot decide, for example because the AI vendor did not answer. Then it **refuses**. Letting through what nobody could check is exactly what an attacker hopes for.

My counters work the other way round. If the message limit or the dust ledger cannot be read because of a technical fault, I **serve the customer** anyway. A fault of mine is not theirs to pay for.

## How fast a customer may write {#how-fast-a-customer-may-write}

I count the messages of every conversation over the last minute, hour and day. By default a conversation may send me 5 messages a minute, 30 an hour and 80 a day. Past a limit the customer reads a message you write, in your own voice:

```json
"RateLimiting": {
  "Enabled": true,
  "MaxMessagesPerMinute": 5,
  "MaxMessagesPerHour": 30,
  "MaxMessagesPerDay": 80,
  "ErrorMessagePerMinute": "✋ Whoa there! You're casting spells too quickly. Please wait a moment before trying again."
}
```

The message limit bounds how *often* a conversation calls me. The [dust budget]({{ '/dust/' | relative_url }}) bounds how much it *spends*. Keep both.

## The order of my doors

Every message passes my doors in the same order, from the cheapest check to the dearest:

<ol class="steps" markdown="1">
<li markdown="1">

**The mirror's key.** A request without a valid key from a mirror I know is turned away.

</li>
<li markdown="1">

**A known conversation.** A conversation I never opened does not exist for me.

</li>
<li markdown="1">

**The message limit.** Too many messages and the customer reads your refusal.

</li>
<li markdown="1">

**The dust.** An empty pouch and the conversation is over.

</li>
<li markdown="1">

**The guard, the diviner and the witch,** as in [Actors]({{ '/actors/' | relative_url }}).

</li>
</ol>

## When I refuse to start {#when-i-refuse-to-start}

A plugin describes its world in three places that nothing else ties together: the seals in C#, the tomes in `agents.json` and your settings. Each can be right alone and wrong together. If I let that pass, a customer would meet a conversation that never answers. So I check everything when I wake up and I refuse to start, naming the fault, when something does not fit.

The refusals you will meet on your first day:

| What I say is wrong | What to do |
|---|---|
| A setting still holds `_SECURE_OVERRIDE_` or `_FUNCTIONAL_OVERRIDE_` | Give it a real value: a key, a vendor, a model |
| A model is not in its vendor's list | Fix the name or give its prices: [case B]({{ '/dust/#case-b' | relative_url }}) |
| A subject has no witch, or a witch has no subject | Add the missing class or the missing intent in `agents.json` |
| A spell or one of its ingredients has no description | Add the `[Description]`: it is all the AI sees |
| A witch has no `[RequiresLLMTier]` | Say which of the three levels she needs |
| Two plugins claim the same subject | Rename one of them |
| A key is shorter than 32 bytes, or a name appears twice in the list of mirrors | Forge a new key with `openssl rand -base64 32` |
| A mirror's `CallbackOrigins` holds a path, a query or a scheme other than http(s) | Write the scheme, host and port alone, such as `http://grimoire:5004` |
| A witch asks a sister I cannot find | Check the name in `[ConsultsAgent]` and, for a faraway coven, its entry in `Partners` |

In all, I run nine families of checks. Every one of them guards the same thing: an installation that looks fine and then fails in front of a customer.

My [alembic]({{ '/alembic/' | relative_url }}) runs the same checks while you are still writing your tomes, when a fix costs nothing.
