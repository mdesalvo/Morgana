---
title: "🤖 Agents"
permalink: /agents/
eyebrow: Pillar 2 of 6
tagline: Declarative specialization with automatic discovery and dynamic capabilities (MCP + A2A)
lede: My agents are the witches of my coven. Each one is a specialist in one corner of your world. Let me show you what a witch is made of, where her spells come from and how she asks a sister for help.
---

## One witch, one subject

Each witch of my coven looks after one **subject** your customers ask about: invoices, contracts, orders, plant care. In code a subject is called an **intent** and a witch is an **agent**. My diviner, from the [previous chapter]({{ '/actors/' | relative_url }}), decides which subject a message belongs to and the witch of that subject answers.

My example shop has four:

| Witch | Looks after | Can she change anything? |
|---|---|---|
| 🧾 Billing | Invoices, what is still owed, payments received | No, she only reads |
| 📜 Contract | The garden-care plan, its clauses and visits | Yes, she can sign a customer up |
| 🌿 Inventory | The catalogue, the stock and the orders | Yes, she creates and confirms orders |
| 🪴 Gardening | Plant care advice | No, she reads an almanac |

## What a witch is made of

Three things, each written by a different kind of person:

<div class="cards three">
<div class="card"><span class="kind">Words</span><h3>Her tome</h3><p>What she does, her character and how she works, in plain words. Most of a witch is this. See <a href="{{ '/prompts/' | relative_url }}">Prompts</a>.</p></div>
<div class="card"><span class="kind">Actions</span><h3>Her spells</h3><p>The actions she can take on your systems: read an invoice, create an order. In code they are <strong>tools</strong>. A developer writes them or they come from a spell server.</p></div>
<div class="card"><span class="kind">Code</span><h3>Her seal</h3><p>A tiny class that says which subject she covers and which AI model she needs. Nothing more.</p></div>
</div>

The seal really is tiny. This is the whole of my accounts witch:

```csharp
[HandlesIntent("billing")]                // the subject she answers for
[RequiresLLMTier(LLMTier.Efficiency)]     // the AI model she needs
[ConsultsAgent("inventory")]              // a sister she may ask
public class BillingAgent : MorganaAgent { /* a constructor and nothing else */ }
```

When I wake up I find every witch by her seal. Nobody has to register her by hand. If a subject has no witch, or a witch has no subject, I refuse to open my book and I say which one, so the mistake never reaches a customer.

### The model a witch needs

I work with three levels of AI model, which you choose when you [install me]({{ '/dust/' | relative_url }}):

- **Economy**: the cheapest, fine for short and simple work like my garden witch's advice
- **Efficiency**: the everyday choice for most witches, like accounts and contracts
- **Performance**: the strongest and dearest, kept for a witch who truly needs deep reasoning

Each witch states her level and gets exactly that model. I never slip her a cheaper one behind your back.

## Spells: how a witch acts

A witch who only talks is of little use. Spells let her read and change things in your systems. A spell is a method with a description that tells the AI what it does and when to cast it:

```csharp
[ProvidesToolForIntent("billing")]
public class BillingTool : MorganaTool
{
    [Description("Retrieves the customer's recent nursery invoices...")]
    public async Task<string> GetInvoices(
        [Description("The customer's own identifying code...")]
        [ToolParameter(Records.ToolScope.SharedContext)] string customerCode,
        [Description("Number of recent invoices to retrieve (1-10)...")]
        [ToolParameter(Records.ToolScope.Request)] int count) { /* reads the shop's books */ }
}
```

The descriptions are all the AI ever sees of a spell, so they deserve care. The scope on each ingredient tells me where its value comes from:

| Scope | Meaning |
|---|---|
| `Request` | Ask the customer |
| `Context` | Keep it in this witch's memory and ask only if it is not known yet |
| `SharedContext` | The same, but my whole coven can see it. The customer code is shared, so no witch asks for it twice. See [Context]({{ '/context/' | relative_url }}) |

Every witch also knows a few spells of mine: she can remember a value, show buttons, show a card and tell me her work is done.

## Spells from outside: MCP

Plenty of spells already exist out there, kept by others. The **Model Context Protocol** (MCP) is an open standard for offering tools to AI. A witch can borrow her spells from an MCP server just by naming it:

```csharp
[HandlesIntent("gardening")]
[RequiresLLMTier(LLMTier.Economy)]
[UsesMCPServer("http://localhost:5006/mcp")]
public class GardeningAgent : MorganaAgent { /* constructor only */ }
```

My garden witch has no spells of her own. Hers come from a little almanac server I carry with my shop. To the AI they look like any other spell.

Most often MCP adds to a witch's own spells rather than replacing them. I test every MCP server before I trust it: one that tries to steer my witches with hidden instructions is banished from all of them. More about that in [Safety and limits]({{ '/safety/' | relative_url }}).

{% include shot.html src="cauldron/gardening.jpg" caption="My garden witch answers with spells borrowed from her MCP server" %}

## Sisters who ask each other

Sometimes a witch needs what a sister knows. A customer asks my accounts witch *"what was in the order behind my last invoice?"*. The invoice is hers but the order belongs to my stock witch.

With `[ConsultsAgent("inventory")]` my accounts witch may ask my stock witch a question behind the scenes. The customer asks once and gets one answer.

<div class="flow across">
  <div class="step"><b>Customer</b><span>asks Billing one question</span></div>
  <div class="arrow"></div>
  <div class="step"><b>Billing</b><span>asks Inventory about the order</span></div>
  <div class="arrow"></div>
  <div class="step"><b>Inventory</b><span>answers her sister</span></div>
  <div class="arrow"></div>
  <div class="step"><b>Billing</b><span>gives the customer one answer</span></div>
</div>

My witches speak to each other with the open **A2A** (agent-to-agent) protocol. A sister in my own coven needs no setup at all. A sister can even live in **a faraway coven**, a witch who masters a craft of her own whom I may consult if you let me. Then she is named together with her coven, `[ConsultsAgent("shipping", "acme")]`. The two covens share a secret key. [Faraway covens]({{ '/partners/' | relative_url }}) tells that story.

## When a witch lets go

A witch keeps the conversation until she judges the customer's request settled. Then she hands it back to me and I add a closing line. Whenever a witch shows buttons, I add my own way out beneath them, so no customer is ever trapped with one witch.
