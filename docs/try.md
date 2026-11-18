---
title: Summon me in five minutes
permalink: /try/
eyebrow: Welcome
lede: Let me show you what I can do before you write a single line. You need Docker, the .NET 10 SDK and a key from one AI vendor. The rest I carry with me.
---

I always travel with a small enchanted shop: **The Greenhouse & Nursery**, which sells plants and a garden-care plan. Four witches of my coven work there and you can talk to them right away.

## The summoning

<ol class="steps" markdown="1">
<li markdown="1">

**Fetch my book and its settings**

```bash
git clone https://github.com/mdesalvo/Morgana.git
cd Morgana
cp development.env.template .env
```

</li>
<li markdown="1">

**Forge my keys**

With the first key I lock away every conversation I keep. With one more for each mirror, that mirror proves to me that it is mine. Forge each one with this command and paste them into `.env` as `ENCRYPTIONKEY`, `CAULDRON_SYMMETRIC_KEY`, `GRIMOIRE_SYMMETRIC_KEY` and `RUNE_SYMMETRIC_KEY`.

```bash
openssl rand -base64 32
```

</li>
<li markdown="1">

**Give me three AI models**

I think at three levels: a cheap one for quick chores, a mid-range one for everyday work and a strong one for the hardest questions. All three can come from the same vendor. Uncomment the block in `.env` and put your key in it:

```bash
LLM_ECONOMY_PROVIDER=Anthropic
LLM_ECONOMY_APIKEY=sk-ant-...
LLM_ECONOMY_MODEL=claude-haiku-5-5

LLM_EFFICIENCY_PROVIDER=Anthropic
LLM_EFFICIENCY_APIKEY=sk-ant-...
LLM_EFFICIENCY_MODEL=claude-sonnet-5-5

LLM_PERFORMANCE_PROVIDER=Anthropic
LLM_PERFORMANCE_APIKEY=sk-ant-...
LLM_PERFORMANCE_MODEL=claude-opus-5-5

ACTORSYSTEM_TIER=Economy
```

I speak just as well with OpenAI, Google, Azure OpenAI and Ollama. [Models and dust]({{ '/dust/' | relative_url }}) tells you more about my three levels.

</li>
<li markdown="1">

**Light the fire**

```bash
dotnet build ./Morgana
dotnet build ./Channels/Cauldron
docker compose --env-file .env --env-file .env.versions build
docker compose --env-file .env --env-file .env.versions up
```

Open **<http://localhost:5002>**. I will be there to greet you.

</li>
</ol>

> If I vanish right after starting, read the last lines of my log. I refuse to open my book while an ingredient is missing or still holds the template's value and I say which one. [Safety and limits]({{ '/safety/#when-i-refuse-to-start' | relative_url }}) lists the usual culprits.
{: .tip}

## Talk to my shop

The customer I know best is **P994E**. Try these, one after the other, in the same conversation:

| Say | What I do |
|---|---|
| *Show me my invoices* | My accounts witch takes over and asks for your customer code, once |
| *What does my care plan cover?* | My contracts witch answers without asking for the code again |
| *What was in the order behind my last invoice?* | My accounts witch quietly asks my stock witch and gives you one answer |
| *My roses are wilting, what should I do?* | My garden witch reads an almanac kept by an outside spell server |
| *I'd like to order two lavender bundles* | My stock witch prepares the order and asks you to confirm it |
| *What's the weather on Mars?* | Nobody in my coven knows that. I answer myself and show what we can do |

<div class="shots two">
{% include shot.html src="cauldron/invoice-card.jpg" caption="My accounts witch shows an invoice as a card" %}
{% include shot.html src="cauldron/gardening.jpg" caption="My garden witch answers from her almanac" %}
</div>

## Look into a terminal mirror

Two more of my mirrors live in a terminal. Open one at a time, while I am running:

```bash
dotnet build ./Channels/Grimoire
docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases grimoire
```

Write `rune` in place of `grimoire` for my plain-text mirror. Keep `--use-aliases`: without it I cannot find the terminal to send it my answers.

## Let me rest

```bash
docker compose --env-file .env --env-file .env.versions down
```

I keep the conversations in a Docker volume, so the next time you summon me I remember them.

## Where next

<div class="cards">
<a class="card" href="{{ '/actors/' | relative_url }}"><h3>Understand what you just saw</h3><p>My six pillars, starting with the familiars who carry a message.</p></a>
<a class="card" href="{{ '/authoring/' | relative_url }}"><h3>Make me speak for your world</h3><p>Write your own tomes in place of my shop.</p></a>
</div>
