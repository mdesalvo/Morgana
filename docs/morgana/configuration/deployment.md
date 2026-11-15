---
title: "On your side: deployment"
permalink: /morgana/configuration/deployment/
eyebrow: Configuration · the operator decides
lede: The values only whoever runs Morgana can supply. Secrets and models come first and everything after them is tuning.
---

<div class="pills"><span class="pill you">Required: secrets</span><span class="pill you">Required: three tiers</span><span class="pill">Optional: everything else</span></div>

## 1 · Secrets

| Variable | Purpose | Generate with |
|---|---|---|
| `ENCRYPTIONKEY` | AES-256 key for the conversation databases, base64 of 32 bytes | `openssl rand -base64 32` |
| `JWT_SYMMETRIC_KEY` | HMAC-SHA256 key shared between a channel and its entry in Morgana's `Issuers[]` | `openssl rand -base64 32` |

> Lose the encryption key and the stored conversations are unreadable. Keep it in a secret store and back it up apart from the data.
{: .warn}

## 2 · The three tiers

Every deployment declares **all three** tiers. Each is bound to a provider by name and the providers need not match.

| Tier | Typical use | Example model |
|---|---|---|
| `Economy` | Framework actors and simple agents | A small, fast model |
| `Efficiency` | Ordinary domain agents | A mid-range model |
| `Performance` | Agents with a stated need for deep reasoning | A top model |

```json
"LanguageModels": {
  "Reliability": { "TimeoutSeconds": 60, "MaxRetries": 2 },
  "Tiers": {
    "Economy": {
      "Provider": "Anthropic",
      "Connection": { "ApiKey": "..." },
      "Options": { "ModelId": "claude-haiku-5-5" }
    },
    "Efficiency":  { "Provider": "Anthropic", "Connection": { "ApiKey": "..." }, "Options": { "ModelId": "claude-sonnet-5-5" } },
    "Performance": { "Provider": "Anthropic", "Connection": { "ApiKey": "..." }, "Options": { "ModelId": "claude-opus-5-5" } }
  }
}
```

The same in Docker is a flat set of variables per tier, for example `LLM_ECONOMY_PROVIDER`, `LLM_ECONOMY_APIKEY`, `LLM_ECONOMY_ENDPOINT` and `LLM_ECONOMY_MODEL`. The same goes for `EFFICIENCY` and `PERFORMANCE`.

### Which provider reads which field

| Provider | `ApiKey` | `Endpoint` | `ModelId` |
|---|---|---|---|
| Anthropic | ✅ | | Model id |
| OpenAI | ✅ | | Model id |
| Google | ✅ | | Model id |
| AzureOpenAI | ✅ | ✅ | **Deployment name** |
| Ollama | | ✅ | Local model name |

### Why there is no fallback

An agent declares its tier with `[RequiresLLMTier]`. If Morgana quietly served a `Performance` agent from a cheaper tier the failure would be silent: the agent would still answer, only worse. So a missing tier is a startup error and never a downgrade.

### Choosing

- **Start cheap and move up on evidence.** Put everything on `Economy` or `Efficiency` and let the [PromptHarness]({{ '/prompt-harness/' | relative_url }}) tell you what regresses.
- **`Performance` is for existential needs.** An agent whose author can argue deep reasoning is the job, never a nice-to-have upgrade.
- **Mix vendors freely.** A local Ollama model for `Economy` and a hosted model above it is a valid shape.

### Prices and ceilings

The whole story, with the catalogue of every vendor, is on [Dust, models and pricing]({{ '/morgana/configuration/dust-and-models/' | relative_url }}). In short:

A tier inherits the price and output ceiling of the model it names from the provider's catalogue. Where the model is not listed (every Azure deployment and any local model) or where you negotiated a discount, set the four prices and any ceiling yourself:

```text
Morgana__LanguageModels__Tiers__{Tier}__Pricing__InputUsdPerMillion
Morgana__LanguageModels__Tiers__{Tier}__Pricing__OutputUsdPerMillion
Morgana__LanguageModels__Tiers__{Tier}__Pricing__CacheReadUsdPerMillion
Morgana__LanguageModels__Tiers__{Tier}__Pricing__CacheWriteUsdPerMillion
Morgana__LanguageModels__Tiers__{Tier}__Options__MaxOutputTokens
```

An Anthropic tier must end up with an output ceiling.

## 3 · The framework tier

`ActorSystem:Tier` names the tier on which Morgana's own actors run: guard, classifier, presenter and channel adapter. It is mandatory. `Economy` is the shipped value and it works while your prompts stay short and direct.

## 4 · Channels and their keys

Each channel is one `Issuers[]` entry whose `Name` equals the channel's own issuer and whose key matches its signing secret.

```json
"Authentication": {
  "Audience": "morgana.ai",
  "Issuers": [
    { "Name": "cauldron", "SymmetricKey": "..." },
    { "Name": "grimoire", "SymmetricKey": "..." },
    { "Name": "rune",     "SymmetricKey": "..." }
  ]
}
```

Keys must be at least 256 bits and no name may appear twice. This list holds **channels and nothing else**: a channel key never opens the agent-to-agent endpoints. Give each channel its own key in production. The shipped compose file shares one only for convenience.

## 5 · Storage

Point `ConversationPersistence:StoragePath` at a persistent volume. In the shipped compose file that is the `morgana-data` volume mounted at `/app/data`.

## 6 · Limits and messages

Tune these to your audience. The words are yours too: every limit message is authored configuration and a good one sounds like your brand.

| Section | You set |
|---|---|
| `RateLimiting` | Messages per minute, hour and day and the three refusal texts |
| `DustLimiting` | The budget per conversation and the 70 %, 90 % and lockout messages |
| `HistoryReducer` | When to summarise and the summarisation prompt |
| `ActorSystem` | Timeout, which guards are on, the collision threshold |

## 7 · Observability

Choose where traces and metrics go. Exporters are declared by name.

```json
"OpenTelemetry": {
  "Enabled": true,
  "ServiceName": "Morgana",
  "Exporters": [
    { "Name": "console", "Enabled": true },
    { "Name": "otlp", "Enabled": true, "Endpoint": "http://localhost:4317" }
  ]
}
```

Message and response text are left off spans unless you set `EnableSensitiveData`. See [observability]({{ '/morgana/operations/observability/' | relative_url }}).

## 8 · Partners (only when federating)

Talking to another Morgana needs a public address, a partner entry and a policy per direction. All of it is covered on the [agent to agent page]({{ '/morgana/operations/a2a/' | relative_url }}).

## 9 · The browser widget

Cauldron's embeddable widget refuses to be framed until you list the sites that may host it in `Cauldron:Widget:AllowedEmbedOrigins`. See [Cauldron]({{ '/channels/cauldron/' | relative_url }}).

## Pre-flight checklist

- [ ] Both secrets generated and stored
- [ ] All three tiers declared with a provider and a model
- [ ] `ActorSystem:Tier` names a tier
- [ ] One issuer per channel with a key of 256 bits or more
- [ ] Storage on a persistent volume
- [ ] Limits and refusal messages reviewed
- [ ] Exporters pointing somewhere someone looks
- [ ] Partners declared and `PublicUrl` set, if federating
