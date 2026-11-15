---
title: Models and dust
permalink: /dust/
eyebrow: Installing me
lede: Magic dust is the coin I spend on AI. Let me tell you what it is, how it matches what your AI vendor bills you and the few cases in which you, the installer, need to touch a price.
---

> **In the usual case you set nothing here.** Pick one of my five built-in vendors and a model from its list: I already know the price and the exchange works by itself. Read on to understand the coin, or jump to [when you need to act](#when-you-need-to-act).
{: .tip}

## Dust is like casino chips

At a casino, money never changes hands at the tables. You buy chips at the desk and every table takes chips. I work the same way.

- I hand every conversation a **pouch of dust**: 100 by default.
- Every call I make to an AI model spends a pinch of it, whether my guard is checking a message or a witch is writing an answer.
- When the pouch is empty, the conversation is over. The customer can start a new one.

Why not count in the vendor's own unit, the **token** (roughly a word fragment)? Because a token costs a different amount on every model. A conversation that used a cheap model ten times and a strong one twice cannot be added up in tokens. In dust it can: one coin for all my magic.

The chip has a fixed value in money:

> **1 dust = US$ 0.015.** So the default budget of 100 dust is **US$ 1.50** per conversation. That is the number you are really choosing when you set the budget.
{: .tip}

## Each level has its own exchange rate

Like a casino where the same chip buys one spin at a cheap table and a fraction of a spin at a high-stakes one, dust buys many tokens of a cheap model and few of a strong one. Each of my three levels exchanges at the rate of its own model:

| Level and model | Price per million tokens in · out | Tokens one dust buys in · out |
|---|---|---|
| **Economy** · `claude-haiku-5-5` | US$ 0.10 · 0.50 | 150 000 · 30 000 |
| **Efficiency** · `claude-sonnet-5-5` | US$ 2.00 · 10.00 | 7 500 · 1 500 |
| **Performance** · `claude-opus-5-5` | US$ 4.00 · 20.00 | 3 750 · 750 |

The rule behind the table is one line: **tokens per dust = 15 000 ÷ the price per million tokens**. That is just US$ 0.015 written in tokens.

### The small equation

For each AI call, the vendor reports how many tokens were read and written. I charge:

```text
dust = tokens read  ÷ tokens per dust (in)
     + tokens written ÷ tokens per dust (out)
```

Vendors also keep recently read text in a **cache** and charge less to read it again. I count cached tokens at the same discount the vendor gives, so my dust always matches the bill.

### One call, worked out

A witch on **Efficiency** reads 20 000 tokens and writes 500. Of the tokens read, 15 000 come from the cache.

| Part | Tokens | Counts as | Dust |
|---|---|---|---|
| Fresh reading | 5 000 | 5 000 | 5 000 ÷ 7 500 = 0.67 |
| Cached reading | 15 000 | 750, since the cache costs 5 % of fresh reading | 750 ÷ 7 500 = 0.10 |
| Writing | 500 | 500 | 500 ÷ 1 500 = 0.33 |
| **Total** | | | **1.10 dust = US$ 0.0165** |

Checked against the vendor's prices directly: the same call costs US$ 0.0165. The two always agree.

## What the customer sees

Cauldron and Grimoire show a gauge in their header that goes from 100 % down to 0.

| When | The customer reads |
|---|---|
| 70 % is spent | A gentle warning, once |
| 90 % is spent | A second warning, once |
| It is all spent | The conversation is complete. They can start a new one |

You write all three messages in `Morgana:DustLimiting`. The pouch lasts the whole life of the conversation and is never refilled.

> If my dust ledger cannot be read or written for a technical reason, I keep serving the customer: a fault of mine is not theirs to pay for. The [message limit]({{ '/safety/#how-fast-a-customer-may-write' | relative_url }}) still applies.
{: .tip}

## Choosing a budget

Start from money: decide the most a conversation may cost you and divide by 0.015.

| A conversation may cost at most | `BudgetPerConversation` |
|---|---|
| US$ 0.30 | 20 |
| US$ 0.75 | 50 |
| **US$ 1.50** (default) | **100** |
| US$ 3.00 | 200 |
| US$ 7.50 | 500 |

How many turns that buys depends on your models and on how long your agents' texts are. Talk to me for a few turns and watch the gauge: it tells the truth faster than any estimate.

## Where prices come from

Each vendor's connector carries a **price list** of its models, which I call the catalogue. For every model it records the four prices I need (reading, writing, cache reading, cache writing, in US dollars per million tokens) and the longest answer the model can write.

When I wake up, each level looks up the model it names in its vendor's list and takes its price from there. If the model is not in the list and you gave no price, I refuse to start. I never run a level I cannot charge for.

## When you need to act {#when-you-need-to-act}

| Your situation | What you do |
|---|---|
| A built-in vendor and a model in its list | **Nothing.** [Case A](#case-a) |
| The vendor released a model newer than me | Give its four prices. [Case B](#case-b) |
| You pay a negotiated or discounted price | Give the prices your agreement changes. [Case C](#case-c) |
| You use Azure OpenAI | Give the prices of every Azure level. [Case D](#case-d) |
| You run a model on your own hardware | Give zeros. [Case E](#case-e) |
| You want shorter answers than the model allows | Lower the ceiling. [Case F](#case-f) |

Prices are given on the level, under `Morgana:LanguageModels:Tiers:{level}:Pricing`. Whatever you write there wins over the catalogue, one value at a time. In Docker they go in a `docker-compose.override.yml` beside the shipped compose file, which you then never need to edit.

### Case A · the model is in the list {#case-a}

Name it and you are done:

```bash
LLM_EFFICIENCY_PROVIDER=Anthropic
LLM_EFFICIENCY_APIKEY=sk-ant-...
LLM_EFFICIENCY_MODEL=claude-sonnet-5-5
```

### Case B · a model newer than me {#case-b}

The vendor launches a model on Tuesday and I was built the week before. You need not wait for my next release. If you simply name the model, I stop at startup with a clear message:

```text
Morgana:LanguageModels:Tiers:Efficiency:Options:ModelId 'claude-sonnet-5-6' is not in the
models.json that provider 'Anthropic' ships with this release. If Anthropic has retired it,
choose a model that models.json lists. If it is newer than this release, declare its price
under Morgana:LanguageModels:Tiers:Efficiency:Pricing.
```

A typo in the model's name lands here too. Take the four prices from the vendor's price page and add them:

```bash
# .env
LLM_EFFICIENCY_MODEL=claude-sonnet-5-6
```

```yaml
# docker-compose.override.yml
services:
  morgana:
    environment:
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__InputUsdPerMillion=2.50
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__OutputUsdPerMillion=12.00
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheReadUsdPerMillion=0.25
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheWriteUsdPerMillion=5.00
      - Morgana__LanguageModels__Tiers__Efficiency__Options__MaxOutputTokens=128000
```

- **All four prices are needed.** If the vendor has no cache prices, write the reading price in both cache fields.
- **On Anthropic the last line is needed too.** Its service requires a ceiling on the length of every answer. The other vendors do not.
- Use a **dot** for decimals, never a comma.

When a later release of mine lists the model, you may delete these lines.

### Case C · a negotiated price {#case-c}

Write only what your agreement changes. For a flat 15 % discount on `claude-sonnet-5-5`, scale all four prices by 0.85:

```yaml
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__InputUsdPerMillion=1.70
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__OutputUsdPerMillion=8.50
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheReadUsdPerMillion=0.085
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheWriteUsdPerMillion=3.40
```

> Change all four together. Cache prices are counted relative to the reading price, so a discount on reading alone would make the cache look dearer than it is.
{: .warn}

### Case D · Azure OpenAI {#case-d}

On Azure the model's name is the name of **your deployment**, which I cannot look up. So the Azure list is empty and every Azure level gives its own prices, taken from your Azure price sheet:

```yaml
      - Morgana__LanguageModels__Tiers__Efficiency__Provider=AzureOpenAI
      - Morgana__LanguageModels__Tiers__Efficiency__Connection__Endpoint=https://my-resource.openai.azure.com
      - Morgana__LanguageModels__Tiers__Efficiency__Options__ModelId=chat-prod-eu
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__InputUsdPerMillion=2.00
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__OutputUsdPerMillion=8.00
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheReadUsdPerMillion=0.50
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheWriteUsdPerMillion=2.00
```

### Case E · a model on your own hardware {#case-e}

A model you run locally with Ollama has no price per token. Give four zeros:

```yaml
      - Morgana__LanguageModels__Tiers__Economy__Provider=Ollama
      - Morgana__LanguageModels__Tiers__Economy__Connection__Endpoint=http://localhost:11434
      - Morgana__LanguageModels__Tiers__Economy__Options__ModelId=llama3.2
      - Morgana__LanguageModels__Tiers__Economy__Pricing__InputUsdPerMillion=0
      - Morgana__LanguageModels__Tiers__Economy__Pricing__OutputUsdPerMillion=0
      - Morgana__LanguageModels__Tiers__Economy__Pricing__CacheReadUsdPerMillion=0
      - Morgana__LanguageModels__Tiers__Economy__Pricing__CacheWriteUsdPerMillion=0
```

That level then spends no dust at all. If your hardware is not free for you, write what a million tokens really costs you in power and wear and the budget protects that too. Ollama's **cloud** models are different: they are in the list and priced like any hosted model.

### Case F · shorter answers {#case-f}

Each model has its own ceiling on how long one answer may be. A level may lower it:

```yaml
      - Morgana__LanguageModels__Tiers__Efficiency__Options__MaxOutputTokens=4096
```

### Checking your numbers

After changing a price, have a few conversations with me and compare two measures in your monitoring tools: `morgana.dust.consumed` and the token counts `gen_ai.usage.*`. Dust divided by tokens should match the rate you expect for that level. Far too little usually means a price left at zero. Far too much usually means a comma where a dot belongs.

## The price lists

| Vendor | Models in the list | What you give |
|---|---|---|
| **Anthropic** | 14 | Nothing for a listed model. A newer one needs four prices and `MaxOutputTokens` |
| **OpenAI** | 18 | Nothing for a listed model. A newer one needs four prices |
| **Google** | 7 | Nothing for a listed model. A newer one needs four prices |
| **Azure OpenAI** | none, on purpose | Four prices on every level |
| **Ollama** | 22 cloud models | Nothing for a cloud model. Zeros for a local one |

Below is what each list holds in this release, in US dollars per million tokens. The list inside each connector is the reference and it is refreshed before every release.

<details markdown="1">
<summary>Anthropic · 14 models · lineup checked 2026-11-09</summary>

| ModelId | Max output | Input | Output | Cache read | Cache write |
|---|---|---|---|---|---|
| `claude-fable-5-2` | 128 000 | 8.00 | 40.00 | 0.20 | 16.00 |
| `claude-fable-5-1` | 128 000 | 10.00 | 50.00 | 0.25 | 20.00 |
| `claude-fable-5` | 128 000 | 10.00 | 50.00 | 1.00 | 20.00 |
| `claude-opus-5-5` | 128 000 | 4.00 | 20.00 | 0.20 | 8.00 |
| `claude-opus-5` | 128 000 | 5.00 | 25.00 | 0.50 | 10.00 |
| `claude-opus-4-8` | 128 000 | 5.00 | 25.00 | 0.50 | 10.00 |
| `claude-opus-4-7` | 128 000 | 5.00 | 25.00 | 0.50 | 10.00 |
| `claude-opus-4-6` | 128 000 | 5.00 | 25.00 | 0.50 | 10.00 |
| `claude-opus-4-5` | 64 000 | 5.00 | 25.00 | 0.50 | 10.00 |
| `claude-sonnet-5-5` | 128 000 | 2.00 | 10.00 | 0.10 | 4.00 |
| `claude-sonnet-5` | 128 000 | 2.00 | 10.00 | 0.20 | 4.00 |
| `claude-sonnet-4-6` | 128 000 | 3.00 | 15.00 | 0.30 | 6.00 |
| `claude-haiku-5-5` | 128 000 | 0.10 | 0.50 | 0.01 | 0.20 |
| `claude-haiku-4-5` | 64 000 | 1.00 | 5.00 | 0.10 | 2.00 |

</details>

<details markdown="1">
<summary>OpenAI · 18 models · lineup checked 2026-11-09</summary>

| ModelId | Max output | Input | Output | Cache read | Cache write |
|---|---|---|---|---|---|
| `gpt-6.1-sol` | 128 000 | 2.00 | 10.00 | 0.10 | 2.50 |
| `gpt-6.1-terra` | 128 000 | 1.00 | 5.00 | 0.05 | 1.25 |
| `gpt-6-astra` | 128 000 | 10.00 | 50.00 | 1.00 | 12.50 |
| `gpt-6-sol` | 128 000 | 2.00 | 10.00 | 0.20 | 2.50 |
| `gpt-6-luna` | 128 000 | 0.10 | 0.50 | 0.01 | 0.125 |
| `gpt-5.6-sol` | 128 000 | 4.00 | 20.00 | 0.40 | 5.00 |
| `gpt-5.6-terra` | 128 000 | 2.00 | 12.00 | 0.20 | 2.50 |
| `gpt-5.6-luna` | 128 000 | 0.20 | 1.20 | 0.02 | 0.25 |
| `gpt-5.5` | 128 000 | 5.00 | 30.00 | 0.50 | 5.00 |
| `gpt-5.4` | 128 000 | 2.50 | 15.00 | 0.25 | 2.50 |
| `gpt-5.4-mini` | 128 000 | 0.75 | 4.50 | 0.075 | 0.75 |
| `gpt-5.4-nano` | 128 000 | 0.20 | 1.25 | 0.02 | 0.20 |
| `gpt-5.2` | 128 000 | 1.75 | 14.00 | 0.175 | 1.75 |
| `gpt-5.1` | 128 000 | 1.25 | 10.00 | 0.125 | 1.25 |
| `gpt-4.1` | 32 768 | 2.00 | 8.00 | 0.50 | 2.00 |
| `gpt-4.1-mini` | 32 768 | 0.40 | 1.60 | 0.10 | 0.40 |
| `gpt-4o` | 16 384 | 2.50 | 10.00 | 1.25 | 2.50 |
| `gpt-4o-mini` | 16 384 | 0.15 | 0.60 | 0.075 | 0.15 |

</details>

<details markdown="1">
<summary>Google · 7 models · lineup checked 2026-11-12</summary>

| ModelId | Max output | Input | Output | Cache read | Cache write |
|---|---|---|---|---|---|
| `gemini-4-carbon` | 32 768 | 4.00 | 20.00 | 0.20 | 4.00 |
| `gemini-4-argon` | 32 768 | 4.00 | 20.00 | 0.20 | 4.00 |
| `gemini-4-hydrogen` | 65 536 | 1.50 | 7.50 | 0.075 | 1.50 |
| `gemini-3.8-flash` | 65 536 | 1.50 | 7.50 | 0.15 | 1.50 |
| `gemini-3.6-flash` | 65 536 | 1.50 | 7.50 | 0.15 | 1.50 |
| `gemini-3.5-flash-lite` | 65 536 | 0.30 | 2.50 | 0.03 | 0.30 |
| `gemini-3.1-flash-lite` | 65 536 | 0.25 | 1.50 | 0.025 | 0.25 |

</details>

<details markdown="1">
<summary>Ollama · 22 cloud models · lineup checked 2026-11-13</summary>

| ModelId | Max output | Input | Output | Cache read | Cache write |
|---|---|---|---|---|---|
| `beam` | — | 0.125 | 0.40 | 0.015 | 0.125 |
| `deepseek-v4.1-flash` | — | 0.30 | 1.20 | 0.006 | 0.30 |
| `deepseek-v4-pro` | — | 1.32 | 3.96 | 0.044 | 1.32 |
| `gemma4` | — | 0.14 | 0.40 | 0.05 | 0.14 |
| `glm-5.3` | — | 1.40 | 4.40 | 0.26 | 1.40 |
| `glm-5.3-flash` | — | 0.15 | 0.50 | 0.03 | 0.15 |
| `glm-5.2` | — | 1.40 | 4.40 | 0.26 | 1.40 |
| `gpt-oss:120b` | — | 0.15 | 0.60 | 0.014 | 0.15 |
| `gpt-oss:20b` | — | 0.07 | 0.30 | 0.035 | 0.07 |
| `inkling-2-small` | — | 0.30 | 1.00 | 0.03 | 0.30 |
| `kimi-k3.1-pro` | — | 2.00 | 10.00 | 0.20 | 2.00 |
| `kimi-k3` | — | 3.00 | 15.00 | 0.30 | 3.00 |
| `kimi-k2.7-code` | — | 0.95 | 4.00 | 0.19 | 0.95 |
| `kimi-k2.6` | — | 0.95 | 4.00 | 0.16 | 0.95 |
| `mimo-v2.6-flash` | — | 0.14 | 0.28 | 0.0028 | 0.14 |
| `mimo-v2.6-pro` | — | 0.435 | 0.87 | 0.0036 | 0.435 |
| `minimax-m3.1-flash` | — | 0.15 | 0.60 | 0.015 | 0.15 |
| `minimax-m3` | — | 0.60 | 2.40 | 0.12 | 0.60 |
| `mistral-large-4` | — | 0.70 | 2.10 | 0.07 | 0.70 |
| `muse-spark-open` | — | 1.25 | 4.25 | 0.15 | 1.25 |
| `nemotron-3-super` | — | 0.015 | 0.60 | 0.015 | 0.015 |
| `nemotron-3-ultra` | — | 0.10 | 3.00 | 0.10 | 0.10 |

</details>

<details markdown="1">
<summary>Azure OpenAI · no catalogued models</summary>

The catalogue is empty on purpose. A tier's `ModelId` on Azure names a **deployment you chose** and I cannot know which model sits behind it or what your agreement costs. Declare the four prices of the tier yourself. Endpoints of a classic Azure OpenAI resource and of an Azure AI Foundry project are both served.

</details>
