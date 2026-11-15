---
title: The Greenhouse example
permalink: /examples/
eyebrow: Companion · example plugin
lede: Four agents of a fictional shop and the MCP server one of them draws from. It is what an author reads to learn how a domain is written for Morgana.
---

## The shop

*The Greenhouse & Nursery* sells plants and sells a garden-care subscription. One SQLite database is its system of record and three agents work on it. A fourth reads an almanac through MCP.

<pre class="mermaid">
graph LR
  U["Customer"] --> M["Morgana"]
  M --> B["🧾 Billing<br/>Efficiency · reads"]
  M --> C["📜 Contract<br/>Efficiency · enrols"]
  M --> I["🌿 Inventory<br/>Efficiency · orders"]
  M --> G["🪴 Gardening<br/>Economy · MCP only"]
  B -- consult --> I
  C -- consult --> B
  B & C & I --- DB[("Shop database<br/>SQLite")]
  G --- MCP["GardeningMCP :5006"] --- AL[("Almanac")]
</pre>

| Agent | Intent | Tier | Tools | Colleagues |
|---|---|---|---|---|
| **Billing** | `billing` | Efficiency | `GetInvoices` · `GetInvoiceDetails` · `GetPaymentHistory` · `GetOutstandingBalance` | consults Inventory |
| **Contract** | `contract` | Efficiency | `GetPlanOverview` · `GetContractDetails` · `GetContractClause` · `GetTerminationProcedure` · `GetVisitSchedule` · `SubscribeToGreenCarePlan` | consults Billing |
| **Inventory** | `inventory` | Efficiency | Catalogue and stock and order creation and confirmation and cancellation and history | none |
| **Gardening** | `gardening` | Economy | None of its own. Tools arrive from the MCP server | none |

## What it teaches

- **One organisation and several agents.** Inventory confirming an order and Contract enrolling a plan are the two dispositive actions. Both bill through the same helper so the books agree whichever agent closes a sale. Billing only reads
- **Agents consulting each other.** Billing to Inventory and Contract to Billing form a chain rather than a triangle, so the refusal of a second hop is exercised too
- **Tools acquired at runtime.** Gardening has no tool class and an empty context vocabulary. Its tools come from `GardeningMCP`, which reads the almanac. A plant without a care sheet has no page. A page is found from the customer's own words for the plant
- **Shared context.** The customer code given once reaches every agent
- **Confirmation by seal word.** An order is created pending with a one-time seal word and confirmed only by presenting both

## Try it

The seeded customer who has invoices and a care plan and past orders is `P994E`. The seed calendar is rebased onto the current month at first deployment, so the pending invoice is really pending.

| Say | Watch |
|---|---|
| *"Show me my invoices"* | Billing asks for the code once and answers with a card and buttons |
| *"What does my care plan cover?"* | Contract reuses the code. No second question |
| *"What was in the order behind my last charge?"* | Billing consults Inventory behind the scenes |
| *"My roses are wilting"* | Gardening reads its almanac through MCP |
| *"I'd like to order two lavender bundles"* | A pending order and a confirmation step |
| *"What is the weather on Mars?"* | Nobody's territory. Morgana answers herself |

<div class="shots">
{% include shot.html src="cauldron/invoice-card.jpg" caption="Billing's card for an invoice" %}
{% include shot.html src="cauldron/gardening.jpg" caption="Gardening answering from the almanac" %}
</div>

## Where to read it

| Where | What you learn |
|---|---|
| `Examples/agents.json` | Intents and the prose of four agents, with Territory |
| `Examples/Agents/*.cs` | How little an agent class is |
| `Examples/Tools/*.cs` | Tool contracts and scopes and what a tool returns |
| `Examples/GardeningMCP/` | An MCP server of your own |

> The prose of the example is measured by the [PromptHarness]({{ '/prompt-harness/' | relative_url }}). Change a description or an instruction and run it before you trust the change.
{: .callout}

Everything here ships in the backend image under `plugins/`. Replace it with your own domain when you are ready.
