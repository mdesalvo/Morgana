---
title: Quick start
permalink: /quick-start/
eyebrow: Start
lede: From a clean checkout to a conversation with the example shop. You need Docker and one API key for a language model.
---

Morgana ships with a small fictional domain, *The Greenhouse & Nursery*, already built into the backend image. It gives you four agents to talk to before you write a line of your own. See [the example]({{ '/examples/' | relative_url }}) for what they know.

## With the published images

<ol class="steps" markdown="1">
<li markdown="1">
**Copy the production template**

```bash
cp production.env.template .env
```
</li>
<li markdown="1">
**Fill in the secrets**

Two keys protect the installation and you generate both yourself. Then choose a model for each of the three tiers.

```bash
openssl rand -base64 32   # → ENCRYPTIONKEY
openssl rand -base64 32   # → JWT_SYMMETRIC_KEY
```

In `.env` uncomment one provider per tier. A cheap model for `Economy` and a capable one for `Performance` is the usual shape. The [deployment page]({{ '/morgana/configuration/deployment/' | relative_url }}) explains the three tiers.
</li>
<li markdown="1">
**Start the backend and the browser channel**

```bash
docker compose up
```

Open <http://localhost:5002>. Morgana greets you and offers a button for each agent of the example shop.
</li>
<li markdown="1">
**Try a terminal channel (optional)**

Grimoire and Rune own your terminal, so they start on demand and one at a time.

```bash
docker compose run --rm --service-ports --use-aliases grimoire
docker compose run --rm --service-ports --use-aliases rune
```

> `--use-aliases` is mandatory. Without it the webhook callback from Morgana cannot resolve the channel's name.
{: .warn}
</li>
</ol>

## From source

```bash
cp development.env.template .env
dotnet build ./Morgana && dotnet build ./Channels/Cauldron
docker compose --env-file .env --env-file .env.versions build
docker compose --env-file .env --env-file .env.versions up
```

The backend answers on `https://localhost:5001` and Cauldron on `https://localhost:5002`.

## What to say first

The example customer is `P994E`. Try these with Morgana:

- *"Show me my invoices"* routes to Billing, which asks for the customer code once.
- *"What does my care plan cover?"* routes to Contract and reuses the code you already gave. That is [shared context]({{ '/morgana/pillars/context/' | relative_url }}) at work.
- *"My roses are wilting"* routes to Gardening, whose tools come from an MCP server.
- *"What is the weather on Mars?"* belongs to nobody. Morgana answers herself and shows what she can do.

## Next steps

<div class="cards">
<a class="card" href="{{ '/morgana/pillars/' | relative_url }}"><h3>Understand the design</h3><p>The six pillars in plain language.</p></a>
<a class="card" href="{{ '/morgana/configuration/' | relative_url }}"><h3>Plan a deployment</h3><p>What you decide and what ships decided.</p></a>
<a class="card" href="{{ '/alembic/' | relative_url }}"><h3>Model your domain</h3><p>Let Alembic interview you into a plugin.</p></a>
</div>
