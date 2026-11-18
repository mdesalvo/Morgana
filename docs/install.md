---
title: Your choices
permalink: /install/
eyebrow: Installing me
lede: I arrive with almost everything already brewed. Three ingredients are yours alone to bring. For everything else I have a sensible default and on this page I tell you what each setting is for, so you know when it is worth changing.
---

## What I bring already brewed

Without touching anything, I already come with:

- my whole conversation ritual: guard, diviner, coven, live answers
- the rules my coven follows and my own character
- three guards against abuse and trickery, all of them awake
- sealed storage for every conversation, which survives my restarts
- a message limit and a dust budget for every conversation
- my three mirrors: Cauldron, Grimoire and Rune
- traces of my work that your monitoring tools can read
- connectors for five AI vendors, each with the price list of its models

## The three ingredients only you can bring

<div class="cards three">
<div class="card"><span class="kind">Ingredient</span><h3>Keys</h3><p>One to seal the conversations, one for each mirror I admit.</p></div>
<div class="card"><span class="kind">Ingredient</span><h3>Three AI models</h3><p>A cheap one, a mid-range one and a strong one.</p></div>
<div class="card"><span class="kind">Ingredient</span><h3>A cupboard</h3><p>The folder where I keep the conversations.</p></div>
</div>

Until all three are in the pot, I refuse to start and I name what is missing. That is on purpose: an installation running on the template's sample values is exactly what must never reach a customer.

### 1 · Keys

| Key | What I use it for |
|---|---|
| The **encryption key** | I seal every stored conversation with it. Whoever holds it can read them, so keep it secret. Lose it and the stored conversations are lost too |
| The **mirror key** | Each mirror signs its requests with a key and I only answer mirrors whose key I know. It keeps strangers from talking to me behind your back |

Both are random values you forge yourself:

```bash
openssl rand -base64 32
```

In Docker they go in `.env`: `ENCRYPTIONKEY` and one key per mirror, `CAULDRON_SYMMETRIC_KEY`, `GRIMOIRE_SYMMETRIC_KEY` and `RUNE_SYMMETRIC_KEY`. A leaked key then opens one mirror alone: see [my mirrors](#my-mirrors) below.

### 2 · Three AI models

I think at three levels, from the cheapest model to the most capable. You pick a vendor and a model for each:

| Level | Who uses it | Typical choice |
|---|---|---|
| `Economy` | My own quick chores and simple witches | A small, fast model |
| `Efficiency` | Most witches of my coven | A mid-range model |
| `Performance` | The rare witch who needs deep reasoning | The vendor's strongest model |

Each level names its vendor, its key or address and its model:

```bash
LLM_EFFICIENCY_PROVIDER=Anthropic          # Anthropic, OpenAI, Google, AzureOpenAI or Ollama
LLM_EFFICIENCY_APIKEY=sk-ant-...           # the vendor's API key
LLM_EFFICIENCY_ENDPOINT=                   # only Azure OpenAI and Ollama need an address
LLM_EFFICIENCY_MODEL=claude-sonnet-5-5     # the model's name (on Azure, your deployment's name)
```

The same four lines exist for `ECONOMY` and `PERFORMANCE`. The levels may use different vendors: a local Ollama model for `Economy` with a hosted one above it is perfectly fine. Last, `ACTORSYSTEM_TIER` says which level my own chores run on, normally `Economy`.

Every release of mine is [certified on each vendor]({{ '/prompt-harness/#every-release-of-mine-is-certified' | relative_url }}) with the `Economy` and `Efficiency` models of the moment, so those are the safest choices for the two levels most of my coven works on.

All three levels must be filled, even when no witch uses one of them. I never fall back from one level to another, so a witch who needs a strong model never quietly gets a weak one.

> **Must you set prices?** Not when you pick a model from the vendor's list: I already know what it costs. [Models and dust]({{ '/dust/' | relative_url }}) explains the few cases where you must.
{: .tip}

### 3 · A cupboard

`ConversationPersistence:StoragePath` is the folder where I keep the conversation files. My Docker setup already points it at a volume called `morgana-storage`, so in Docker there is nothing to do. Outside Docker, pick a folder that is backed up.

## How settings reach me

Every setting has a default in my `appsettings.json`. You never edit that file: you **override** values from outside.

- **With Docker**, through the `.env` file next to `docker-compose.yml`, or through a `docker-compose.override.yml` for anything `.env` does not cover
- **Anywhere else**, through environment variables. The path of a setting is written with double underscores: `Morgana:RateLimiting:MaxMessagesPerMinute` becomes `Morgana__RateLimiting__MaxMessagesPerMinute`
- **On a developer's machine**, through .NET user secrets, so no key ever ends up in a commit

A value of `_SECURE_OVERRIDE_` or `_FUNCTIONAL_OVERRIDE_` in my shipped file is a placeholder that means *this one is yours to bring*.

## My mirrors {#my-mirrors}

I keep a list of the mirrors I will speak through, in `Morgana:Authentication:Issuers`. Each entry is a mirror's name and its key:

```json
"Issuers": [
  { "Name": "cauldron", "SymmetricKey": "..." },
  { "Name": "grimoire", "SymmetricKey": "...", "CallbackOrigins": [ "https://localhost:5004" ] },
  { "Name": "rune",     "SymmetricKey": "...", "CallbackOrigins": [ "https://localhost:5003" ] }
]
```

The mirror holds the same key in its own settings. With one key per mirror, a leaked key means changing that mirror alone. A mirror you do not use can leave the list. A [mirror of your own]({{ '/channels/#a-mirror-of-your-own' | relative_url }}) is one more entry. Keys must be at least 32 bytes long, which is what the command above forges.

Grimoire and Rune receive my answers as calls to a web address of their own, so their entries also say where I may call: `CallbackOrigins`, the scheme, host and port alone, without a path. I deliver to no other address, even when a mirror asks me to. Nor do I follow a mirror's address when it sends me somewhere else. Cauldron hears me over SignalR and needs none. My Docker setup already names the addresses Grimoire and Rune have inside its network.

## Every other setting, by purpose

Each of these already has a working default. Open a section when you want to change what it governs.

<details markdown="1">
<summary>Limits: how fast a customer may write to me</summary>

`Morgana:RateLimiting` keeps a single conversation from sending me messages too fast, which shields your AI bill from a runaway script or a misuse. Counts are per conversation.

| Setting | What it is for | Default |
|---|---|---|
| `Enabled` | Switches the limit on or off | on |
| `MaxMessagesPerMinute` · `PerHour` · `PerDay` | The ceilings | 5 · 30 · 80 |
| `ErrorMessagePerMinute` · `PerHour` · `PerDay` | What the customer reads when they hit each one. Write it in your own voice | witchy defaults |

</details>

<details markdown="1">
<summary>Dust: how much a conversation may spend</summary>

`Morgana:DustLimiting` gives every conversation a budget of **magic dust**, my own coin. The customer is warned as it runs low and the conversation ends when it is spent. [Models and dust]({{ '/dust/' | relative_url }}) explains my coin.

| Setting | What it is for | Default |
|---|---|---|
| `Enabled` | Switches the budget on or off | on |
| `BudgetPerConversation` | The budget. 100 dust is US$ 1.50 | 100 |
| `Warning70Message` · `Warning90Message` | The warnings when 70 % and 90 % are spent. `{percent}` is replaced by what is left | witchy defaults |
| `ErrorMessage` | What the customer reads when it is all spent | witchy default |

</details>

<details markdown="1">
<summary>Guards: what I watch</summary>

`Morgana:ActorSystem` holds the switches of my three guards. Leave them awake unless you have your own protection in front of me. [Safety and limits]({{ '/safety/' | relative_url }}) explains each one.

| Setting | What it is for | Default |
|---|---|---|
| `EnableUserGuardrail` | Watches the customers' messages | on |
| `EnableToolGuardrail` | Watches what spells and MCP servers return | on |
| `EnablePeerGuardrail` | Watches questions from faraway covens | on |
| `MaxToolResultLength` | The longest spell result a witch is given, in characters. A longer one is withheld from her | 32000 |

</details>

<details markdown="1">
<summary>Conversation flow</summary>

Also in `Morgana:ActorSystem`. The [Actors]({{ '/actors/#what-you-may-adjust' | relative_url }}) chapter explains them.

| Setting | What it is for | Default |
|---|---|---|
| `Tier` | The AI level of my own chores | `Economy` |
| `TimeoutSeconds` | How long a silence is tolerated before an answer is given up | 180 |
| `IntentCollisionThreshold` | How close two subjects may score before the customer is asked to choose | 0.10 |

</details>

<details markdown="1">
<summary>Answers on screen</summary>

`Morgana:AdaptiveMessaging` controls how my answers are delivered. See [Channels]({{ '/channels/' | relative_url }}).

| Setting | What it is for | Default |
|---|---|---|
| `EnableStreamingResponse` | Answers appear word by word where the mirror can show it | on |
| `RichFeaturesMinLength` | A mirror whose message limit is below this many characters gets no cards and no buttons, because they would not fit | 1000 |

</details>

<details markdown="1">
<summary>Memory and summaries</summary>

`Morgana:HistoryReducer` keeps long conversations affordable. See [Context]({{ '/context/#long-conversations-get-a-summary' | relative_url }}).

| Setting | What it is for | Default |
|---|---|---|
| `Enabled` | Switches summaries on or off | on |
| `SummarizationThreshold` | Messages before the older part is summarised | 12 |
| `SummarizationTargetCount` | Recent messages kept beside the summary | 8 |
| `SummarizationPrompt` | The words I write my summaries from | a careful default |

</details>

<details markdown="1">
<summary>AI vendors: timeouts and retries</summary>

`Morgana:LanguageModels:Reliability` applies to every call I make to every vendor.

| Setting | What it is for | Default |
|---|---|---|
| `TimeoutSeconds` | How long a single AI call may take | 60 |
| `MaxRetries` | How many times a failed call is tried again | 2 |

The three levels themselves are in `Morgana:LanguageModels:Tiers`, described above and in [Models and dust]({{ '/dust/' | relative_url }}).

</details>

<details markdown="1">
<summary>Monitoring</summary>

`Morgana:OpenTelemetry` sends a trace of every turn and a few measures of my work to your monitoring tools. See [Going to production]({{ '/production/#watching-it-run' | relative_url }}).

| Setting | What it is for | Default |
|---|---|---|
| `Enabled` | Switches monitoring on or off | on |
| `ServiceName` | The name I appear under in your tools | `Morgana` |
| `Exporters` | Where the traces go: `console` and `otlp` with its `Endpoint` | both on, OTLP at `localhost:4317` |
| `EnableSensitiveData` | Puts the text of messages in the traces. Leave it off where conversations hold personal data | off |

</details>

<details markdown="1">
<summary>Faraway covens</summary>

`Morgana:AgentToAgent` lets my witches consult faraway witches, masters of other crafts, if you let them. Switched on with no partners, it only lets the witches of my own coven ask each other. See [Faraway covens]({{ '/partners/' | relative_url }}).

| Setting | What it is for | Default |
|---|---|---|
| `Enabled` | Switches the sisterly questions on or off | on |
| `MaxRoundsPerTurn` | How many questions a witch may put to her sisters in one turn | 4 |
| `PublicUrl` | The address partners reach you at, when it differs from the server's own | empty |
| `Partners` | The faraway covens you trust | none |

</details>

<details markdown="1">
<summary>Plugins</summary>

`Morgana:Plugins:Directories` lists the folders I read plugins from: your tomes and the AI vendors' connectors. Default: `plugins`.

</details>

## Before I meet your customers

- [ ] Encryption key created, stored safely and copied apart from the data
- [ ] One key per mirror, the unused mirrors removed from the list
- [ ] Three levels filled with a vendor and a model each
- [ ] Storage on a disk that is backed up
- [ ] Refusal and dust messages written in your own voice
- [ ] Monitoring pointing at a tool somebody looks at
- [ ] Your plugin in `plugins/` and my example shop removed
