---
title: Dust, models and pricing
permalink: /morgana/configuration/dust-and-models/
eyebrow: Configuration · the installer decides
lede: Dust is Morgana's currency of AI consumption. A small equation ties it to the real tokens a model spends and it is worked out for each tier on its own. This page is the whole story for whoever installs Morgana.
---

<div class="pills"><span class="pill ok">Zero work if your models are in the catalogue</span><span class="pill you">Four prices to declare if they are not</span></div>

## The idea

**Think of casino chips.** Real money does not circulate at the tables. You exchange it for chips and everything inside the casino is paid in chips. Each conversation is handed a stack of dust and every model call spends some of it. The exchange desk is the equation below and it posts a **separate rate for each table**: the same stack lasts a long time at a cheap table and melts quickly at a high-stakes one. When the stack is gone that player has finished.

Tokens are the vendors' unit and they are hard to reason about. A conversation that uses a cheap model for ten calls and an expensive one for two cannot be added up in tokens at all. So Morgana spends in her own coin, **dust**, and every call to a model is converted into it.

- **One coin for everything.** A guard check on `Economy` and a long answer on `Performance` are charged in the same unit
- **Per tier.** Each tier converts at its own rate because each tier runs its own model at its own price
- **Pinned to money.** One dust unit is worth **US$ 0.015** by default, so dust is dollars in a form that is easy to budget and to show to a user

> A budget of **100 dust** is a budget of **US$ 1.50** per conversation. That is the number you are really choosing when you set `BudgetPerConversation`.
{: .rule}

> **In the normal case you configure none of this.** Adopt a built-in vendor and a model of its catalogue and the prices, the ceilings and the conversion are already in place. Read on for how it works and for the few cases where you do put a hand to it.
{: .callout}

## The equation

For every model call Morgana reads the usage the vendor reports and computes:

<pre class="mermaid">
graph LR
  U["Usage of one call<br/>fresh input · cache read · cache write · output"] --> E["Input in fresh-token equivalents<br/>fresh + cacheRead × wRead + cacheWrite × wWrite"]
  E --> D1["÷ tokens per dust (input)"]
  U --> D2["output ÷ tokens per dust (output)"]
  D1 --> S(("sum"))
  D2 --> S
  S --> L["Dust charged to the conversation<br/>filed under the role that spent it"]
</pre>

```text
dust = ( fresh + cacheRead × wRead + cacheWrite × wWrite ) / tokensPerDustIn
     +   output / tokensPerDustOut
```

The four constants come from the **price of the model on that tier**, in US dollars per million tokens:

```text
tokensPerDustIn  = 1,000,000 × 0.015 / InputUsdPerMillion
tokensPerDustOut = 1,000,000 × 0.015 / OutputUsdPerMillion
wRead            = CacheReadUsdPerMillion  / InputUsdPerMillion
wWrite           = CacheWriteUsdPerMillion / InputUsdPerMillion
```

This is nothing more than *dollars divided by 0.015* written with tokens. A direction priced at zero is **free**: it is never charged.

### The same coin buys very different amounts

| Tier example | Input $/M | Output $/M | Input tokens per dust | Output tokens per dust | Cache read weight |
|---|---|---|---|---|---|
| `claude-haiku-5-5` on **Economy** | 0.10 | 0.50 | 150 000 | 30 000 | 0.10 |
| `claude-sonnet-5-5` on **Efficiency** | 2.00 | 10.00 | 7 500 | 1 500 | 0.05 |
| `claude-opus-5-5` on **Performance** | 4.00 | 20.00 | 3 750 | 750 | 0.05 |

A thousand output tokens cost about 0.03 dust on the cheap tier and about 1.3 dust on the capable one. That is the whole reason the unit is per tier.

### One worked call

An agent on **Efficiency** (`claude-sonnet-5-5`) sends a prompt of 20 000 tokens, of which 15 000 were served from the cache and none written to it. It receives 500 tokens back.

| Part | Tokens | Weight | Dust |
|---|---|---|---|
| Fresh input | 5 000 | 1 | 5 000 / 7 500 = 0.667 |
| Cache read | 15 000 | 0.05 | 750 / 7 500 = 0.100 |
| Output | 500 | | 500 / 1 500 = 0.333 |
| **Total** | | | **1.10 dust = US$ 0.0165** |

The same call checked in dollars: 5 000 × 2 + 15 000 × 0.10 + 500 × 10 per million is 0.0165. The two always agree.

## What is charged

Every call that goes through a model is metered by the same decorator, so nothing is charged twice and nothing escapes.

| Spent by | Filed under role |
|---|---|
| The framework actors: guard and classifier and presenter and channel adapter | `Morgana` |
| A domain agent and its history reducer | `Morgana (intent/tier)` |
| `/compact` | Its own run |

- **A streamed answer is charged when the stream ends**, however it ends. A turn the user stops is still billed by the vendor and so it is charged here too
- **Usage the vendor does not report is not charged.** A call with no usage report and a call that costs zero write no row
- **Every charge is attributed** in `dust_usage_log`, so you can tell what the guards cost from what the agents cost

## What the user sees

<pre class="mermaid">
stateDiagram-v2
  direction LR
  [*] --> Full: conversation starts
  Full --> Low: 70 % consumed
  Low --> Almost: 90 % consumed
  Almost --> Spent: budget reached
  Spent --> [*]: conversation complete
</pre>

| Moment | The user reads |
|---|---|
| Always | A gauge in the header of Cauldron and Grimoire, from 100 % down to 0 |
| 70 % consumed | `Warning70Message`, shown **once** |
| 90 % consumed | `Warning90Message`, shown **once** |
| Budget reached | `ErrorMessage`. The conversation is complete and they start a new one |

The budget is a **lifetime** budget. It is never reset and exhaustion is terminal for that conversation. The messages are yours to write and `{percent}` is replaced by what is left.

Two edge cases are deliberate. A budget of **zero** is valid and is spent from the start, so nothing is ever admitted. With `DustLimiting:Enabled` off every turn is admitted unmetered and no gauge is shown.

> The dust ledger **fails open**. If it cannot be read or written the user is served and the fault is not theirs to pay for. The rate limiter bounds how often a conversation calls and the dust budget bounds what it consumes: use both.
{: .callout}

## The catalogue

Each provider plugin carries a **catalogue** of its models: a `models.json` embedded in the plugin's DLL. It holds two facts per model and both are what the equation above needs.

```json
{
  "LastUpdate": "2026-11-09T18:58:51Z",
  "Models": [
    {
      "Options": { "ModelId": "claude-sonnet-5-5", "MaxOutputTokens": 128000 },
      "Pricing": {
        "InputUsdPerMillion": 2.00,
        "OutputUsdPerMillion": 10.00,
        "CacheReadUsdPerMillion": 0.10,
        "CacheWriteUsdPerMillion": 4.00
      }
    }
  ]
}
```

| Field | Is |
|---|---|
| `ModelId` | The name a tier writes to run the model. Matched exactly, casing included |
| `MaxOutputTokens` | The ceiling of one answer. A tier may lower it |
| The four `…UsdPerMillion` prices | US dollars per million tokens: fresh input and output and cache read and cache write |
| `LastUpdate` | When the vendor's lineup was last checked against the file |

### How a tier finds its price

<pre class="mermaid">
graph TD
  T["Tier names Options:ModelId"] --> C{"In the provider's<br/>catalogue?"}
  C -- yes --> M["Catalogue entry laid under the tier's Options and Pricing<br/>the tier wins key by key"]
  C -- no --> P{"Tier declares<br/>its own Pricing?"}
  P -- yes --> M
  P -- no --> X["Startup refused:<br/>declare its price under the tier's Pricing"]
  M --> V{"All four prices present<br/>and not negative?"}
  V -- yes --> OK["Tier starts. Dust rates derived"]
  V -- no --> X2["Startup refused naming the missing price"]
</pre>

The result is bound and validated **once at startup**. A model that cannot be priced in every direction does not start. A deployment therefore never runs with an unmetered tier by accident.

## What the installer does

The expected path is the simple one: you adopt a **built-in vendor** and a **model that is in its catalogue**. Then pricing is already operational and you configure nothing about it. This section is for the day that stops being true.

### Do I need to touch pricing?

| Your situation | You do | Jump to |
|---|---|---|
| A built-in vendor and a model of its catalogue | **Nothing** | [Case A](#case-a) |
| The vendor released a model your Morgana version does not know yet | Declare its prices on the tier | [Case B](#case-b) |
| You pay a negotiated or discounted rate | Override the prices your agreement changes | [Case C](#case-c) |
| Azure OpenAI | Declare the prices of every Azure tier | [Case D](#case-d) |
| A local model or one that costs nothing per token | Declare zeros | [Case E](#case-e) |
| You want shorter answers than the model allows | Lower the output ceiling | [Case F](#case-f) |

### Case A · the model is in the catalogue {#case-a}

Name it and stop. The tier inherits its price and its output ceiling.

```json
"Efficiency": {
  "Provider": "Anthropic",
  "Connection": { "ApiKey": "..." },
  "Options": { "ModelId": "claude-sonnet-5-5" }
}
```

The same in the `.env` of the shipped compose file:

```bash
LLM_EFFICIENCY_PROVIDER=Anthropic
LLM_EFFICIENCY_APIKEY=sk-ant-...
LLM_EFFICIENCY_MODEL=claude-sonnet-5-5
```

### Case B · a new model the catalogue does not have yet {#case-b}

The vendor ships a model on Tuesday and your Morgana was built the week before. The catalogue cannot know it. **You do not wait for a release.** Name the model and declare what it costs.

If you forget, Morgana refuses to start and says what to do:

```text
Morgana:LanguageModels:Tiers:Efficiency:Options:ModelId 'claude-sonnet-5-6' is not in the
models.json that provider 'Anthropic' ships with this release. If Anthropic has retired it,
choose a model that models.json lists. If it is newer than this release, declare its price
under Morgana:LanguageModels:Tiers:Efficiency:Pricing.
```

The message names two causes because Morgana cannot tell a retired model from a newer one. A typo in the model id lands here too.

Take the four numbers from the vendor's price page, in US dollars per million tokens. Write them on the tier:

```json
"Efficiency": {
  "Provider": "Anthropic",
  "Connection": { "ApiKey": "..." },
  "Options": {
    "ModelId": "claude-sonnet-5-6",
    "MaxOutputTokens": 128000
  },
  "Pricing": {
    "InputUsdPerMillion": 2.50,
    "OutputUsdPerMillion": 12.00,
    "CacheReadUsdPerMillion": 0.25,
    "CacheWriteUsdPerMillion": 5.00
  }
}
```

The same in Docker. The compose file reads the model from the `.env` and the prices go in a `docker-compose.override.yml` beside it, so the base file is never edited:

```bash
# .env
LLM_EFFICIENCY_MODEL=claude-sonnet-5-6
```

```yaml
# docker-compose.override.yml
services:
  morgana:
    environment:
      - Morgana__LanguageModels__Tiers__Efficiency__Options__MaxOutputTokens=128000
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__InputUsdPerMillion=2.50
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__OutputUsdPerMillion=12.00
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheReadUsdPerMillion=0.25
      - Morgana__LanguageModels__Tiers__Efficiency__Pricing__CacheWriteUsdPerMillion=5.00
```

Three things to know:

- **All four prices are required.** A model that is not priced in every direction does not start
- **The vendor has no cache pricing?** Write the input price for both cache fields. The weights become 1 and cached tokens are charged like fresh ones, which is the honest upper bound
- **`MaxOutputTokens` matters on Anthropic.** Its API needs a ceiling on every call and a model without a catalogue entry has none unless you declare it. Other vendors do not require it

When a later release brings the model into the catalogue you can delete the override. Until you do it still wins, which is fine as long as the numbers are right.

### Case C · a negotiated price {#case-c}

The tier overrides the catalogue **key by key**, so you write only what your agreement changes and keep the rest.

```json
"Efficiency": {
  "Provider": "Anthropic",
  "Connection": { "ApiKey": "..." },
  "Options": { "ModelId": "claude-sonnet-5-5" },
  "Pricing": {
    "InputUsdPerMillion": 1.70,
    "OutputUsdPerMillion": 8.50
  }
}
```

> Override **every** price your discount touches. Cache prices work as weights relative to the input price. A discount written on input alone would make cache reads look dearer than they are and the dust would drift away from your real bill.
{: .warn}

So a flat 15 % off on Sonnet 5.5 is all four numbers scaled by 0.85: input 1.70 and output 8.50 and cache read 0.085 and cache write 3.40.

### Case D · Azure OpenAI {#case-d}

A tier's `ModelId` on Azure is the **deployment name you chose**. Morgana cannot know which model sits behind it or what your agreement says, so the catalogue is empty and **every Azure tier declares its own prices**.

```json
"Efficiency": {
  "Provider": "AzureOpenAI",
  "Connection": {
    "ApiKey": "...",
    "Endpoint": "https://my-resource.openai.azure.com"
  },
  "Options": { "ModelId": "chat-prod-eu" },
  "Pricing": {
    "InputUsdPerMillion": 2.00,
    "OutputUsdPerMillion": 8.00,
    "CacheReadUsdPerMillion": 0.50,
    "CacheWriteUsdPerMillion": 2.00
  }
}
```

Take the numbers from your Azure price sheet for the model behind that deployment. An Azure AI Foundry project endpoint works the same way.

### Case E · a model that costs nothing {#case-e}

A local Ollama model has no per-token price and the catalogue never prices a local model. Declare all four as zero:

```json
"Economy": {
  "Provider": "Ollama",
  "Connection": { "Endpoint": "http://localhost:11434" },
  "Options": { "ModelId": "llama3.2" },
  "Pricing": {
    "InputUsdPerMillion": 0,
    "OutputUsdPerMillion": 0,
    "CacheReadUsdPerMillion": 0,
    "CacheWriteUsdPerMillion": 0
  }
}
```

A direction priced at zero is never charged. That tier **consumes no dust** and conversations that run only on it never move the budget or the gauge. Your protection against runaway use is then the [rate limit]({{ '/morgana/operations/guard-rails/' | relative_url }}) alone. Ollama **cloud** models are different: they are in the catalogue and priced like any hosted one.

If the hardware is not free for you, declare what a million tokens really cost you in power and depreciation instead of zero and the budget will protect that too.

### Case F · a lower output ceiling {#case-f}

The catalogue gives each model its own ceiling. A tier may lower it to bound how long one answer can run.

```json
"Options": { "ModelId": "claude-sonnet-5-5", "MaxOutputTokens": 4096 }
```

### Checking your numbers

After any override, send a few turns and read the two metrics side by side: `morgana.dust.consumed` and `gen_ai.usage.*`. Dust divided by tokens should land on the rate the equation gives for that tier. A tier that charges far less than you expect is usually a price left at zero. A tier that charges far more is usually a decimal comma where a dot belongs: prices are written with a **dot**.

### Each vendor in short

| Vendor | Catalogue | What you declare |
|---|---|---|
| **Anthropic** | 14 models | Nothing for a listed model. A model not yet listed needs its four prices and `MaxOutputTokens` |
| **OpenAI** | 18 models | Nothing for a listed model. A model not yet listed needs its four prices |
| **Google** | 7 models | Nothing for a listed model. A model not yet listed needs its four prices |
| **AzureOpenAI** | none: the file is empty | The four prices of every tier, with the deployment name as `ModelId` |
| **Ollama** | 22 cloud models, priced under their own name. A cloud tag such as `-cloud` is priced under the name it follows | Nothing for a cloud model. For a local model the four prices, usually zeros |

## Choosing a budget

Work backwards from money. Pick what a conversation may cost you at most and divide by 0.015.

| A conversation may cost at most | `BudgetPerConversation` |
|---|---|
| US$ 0.30 | 20 |
| US$ 0.75 | 50 |
| **US$ 1.50** (the shipped value) | **100** |
| US$ 3.00 | 200 |
| US$ 7.50 | 500 |

How far a budget goes depends on your tiers. A typical guarded turn spends a little on `Economy` for the guard and the classifier and more on the agent's own tier. Cheap framework tiers keep the overhead small. Do the sum once with your real models and your real prompts: the gauge and the metric below show the truth.

## Watching what is spent

| Where | Tells you |
|---|---|
| The gauge | The user's remaining level, in whole percent |
| `morgana.dust.consumed` | Dust per call, tagged by the role that spent it and the conversation |
| `gen_ai.usage.*` | The same calls in raw tokens, beside it |
| `dust_budget` and `dust_usage_log` | The lifetime budget and every charge with its attribution, inside each conversation's database |

Reading dust and tokens on one dashboard is the quickest way to check that a price in your configuration is right: dust divided by tokens should land on the rate you expect for that tier.

## The shipped catalogue

A snapshot of what the plugins carry at this release. The `models.json` of each plugin is the source of truth and each file states when its lineup was last checked. Prices are US dollars per million tokens. Cache write is what the vendor charges to put a prompt in its cache. `—` means the catalogue declares no ceiling for that model.

<details markdown="1">
<summary><strong>Anthropic</strong> · 14 models · lineup checked 2026-11-09</summary>

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
<summary><strong>OpenAI</strong> · 18 models · lineup checked 2026-11-09</summary>

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
<summary><strong>Google</strong> · 7 models · lineup checked 2026-11-12</summary>

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
<summary><strong>Ollama</strong> · 22 cloud models · lineup checked 2026-11-13</summary>

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
<summary><strong>Azure OpenAI</strong> · no catalogued models</summary>

The catalogue is empty on purpose. A tier's `ModelId` on Azure names a **deployment you chose** and the framework cannot know which model sits behind it or what your agreement costs. Declare the four prices of the tier yourself. Endpoints of a classic Azure OpenAI resource and of an Azure AI Foundry project are both served.
</details>

## Keeping it current

The catalogue ships with each release of the plugins and the maintainers refresh it against the vendors' official lineups before they publish. A model released after your version is handled by case 2 above: declare its prices on the tier and carry on. Nothing else in the installation depends on the catalogue.
