---
title: Full key reference
permalink: /morgana/configuration/reference/
eyebrow: Configuration
lede: Every setting of Morgana and of its channels in one place. Keys nest with a colon in JSON and with a double underscore in environment variables.
---

<div class="pills"><span class="pill ok">Shipped value shown where there is one</span><span class="pill you">Required means you must supply it</span></div>

## Morgana (backend)

### Language models

| Key | Default | Notes |
|---|---|---|
| `Morgana:LanguageModels:Reliability:TimeoutSeconds` | 60 | Laid under every tier's connection. A tier may not declare it itself |
| `Morgana:LanguageModels:Reliability:MaxRetries` | 2 | The same |
| `Morgana:LanguageModels:Tiers:{Tier}:Provider` | **required** | A registered provider's name: Anthropic, AzureOpenAI, Google, Ollama or OpenAI |
| `Morgana:LanguageModels:Tiers:{Tier}:Connection:ApiKey` | **required** where the provider reads it | |
| `Morgana:LanguageModels:Tiers:{Tier}:Connection:Endpoint` | | Azure OpenAI and Ollama |
| `Morgana:LanguageModels:Tiers:{Tier}:Options:ModelId` | **required** | Model id or Azure deployment name |
| `Morgana:LanguageModels:Tiers:{Tier}:Options:*` | from catalogue | Tunable `ChatOptions`, `MaxOutputTokens` among them. Provider keys go under `AdditionalProperties` |
| `Morgana:LanguageModels:Tiers:{Tier}:Pricing:*` | from catalogue | `InputUsdPerMillion` · `OutputUsdPerMillion` · `CacheReadUsdPerMillion` · `CacheWriteUsdPerMillion` |

`{Tier}` is `Economy`, `Efficiency` or `Performance`. All three are mandatory.

### Actor system

| Key | Default | Notes |
|---|---|---|
| `Morgana:ActorSystem:Tier` | `Economy` | Tier of the framework actors. Mandatory |
| `Morgana:ActorSystem:TimeoutSeconds` | 180 | Budget on silence, renewed by every chunk |
| `Morgana:ActorSystem:EnableUserGuardrail` | true | Moderation of user messages |
| `Morgana:ActorSystem:EnableToolGuardrail` | true | Inspection of tool results and of what MCP servers and partners offer |
| `Morgana:ActorSystem:EnablePeerGuardrail` | true | Inspection of partners' questions |
| `Morgana:ActorSystem:MaxToolResultLength` | 32000 | Longest tool result handed to an agent |
| `Morgana:ActorSystem:IntentCollisionThreshold` | 0.10 | How close two intents rank before the user is asked |

### Agent to agent

| Key | Default | Notes |
|---|---|---|
| `Morgana:AgentToAgent:Enabled` | true | Off means nothing is published and nothing is served |
| `Morgana:AgentToAgent:MaxRoundsPerTurn` | 4 | Consultations in one turn |
| `Morgana:AgentToAgent:PublicUrl` | empty | Declare it only behind an ingress or a published port |
| `Morgana:AgentToAgent:Partners[]` | empty | See [agent to agent]({{ '/morgana/operations/a2a/' | relative_url }}) |

### Authentication, persistence and messaging

| Key | Default | Notes |
|---|---|---|
| `Morgana:Authentication:Audience` | `morgana.ai` | Expected `aud` of every token |
| `Morgana:Authentication:Issuers[]` | **required** | `{ Name, SymmetricKey }` per channel. Channels only |
| `Morgana:ConversationPersistence:EncryptionKey` | **required** | AES-256, base64 of 32 bytes |
| `Morgana:ConversationPersistence:StoragePath` | **required** | Directory of the SQLite files |
| `Morgana:AdaptiveMessaging:EnableStreamingResponse` | true | |
| `Morgana:AdaptiveMessaging:RichFeaturesMinLength` | 1000 | Channels with a shorter message limit lose rich features |

### Limits

| Key | Default | Notes |
|---|---|---|
| `Morgana:RateLimiting:Enabled` | true | Fails open |
| `Morgana:RateLimiting:MaxMessagesPerMinute` | 5 | |
| `Morgana:RateLimiting:MaxMessagesPerHour` | 30 | |
| `Morgana:RateLimiting:MaxMessagesPerDay` | 80 | |
| `Morgana:RateLimiting:ErrorMessagePerMinute` · `PerHour` · `PerDay` · `Default` | authored | The refusal texts |
| `Morgana:DustLimiting:Enabled` | true | Fails open |
| `Morgana:DustLimiting:BudgetPerConversation` | 100 | |
| `Morgana:DustLimiting:Warning70Message` · `Warning90Message` · `ErrorMessage` | authored | `{percent}` is replaced by what is left |

### History, tracing and plugins

| Key | Default | Notes |
|---|---|---|
| `Morgana:HistoryReducer:Enabled` | true | Off hands the model the whole history |
| `Morgana:HistoryReducer:SummarizationThreshold` | 12 | Messages before summarising |
| `Morgana:HistoryReducer:SummarizationTargetCount` | 8 | Messages kept after |
| `Morgana:HistoryReducer:SummarizationPrompt` | authored | |
| `Morgana:OpenTelemetry:Enabled` | true | |
| `Morgana:OpenTelemetry:ServiceName` | `Morgana` | |
| `Morgana:OpenTelemetry:EnableSensitiveData` | false | Message text on spans when true |
| `Morgana:OpenTelemetry:Exporters[]` | console · otlp | `{ Name, Enabled, Endpoint }` |
| `Morgana:Plugins:Directories` | `["plugins"]` | Scanned for domains and providers |

## Cauldron

| Key | Default | Notes |
|---|---|---|
| `Cauldron:MorganaURL` | `https://localhost:5001` | The backend |
| `Cauldron:Authentication:Issuer` | `cauldron` | Must equal an `Issuers[].Name` |
| `Cauldron:Authentication:Audience` | `morgana.ai` | |
| `Cauldron:Authentication:SymmetricKey` | **required** | Matches the backend's entry |
| `Cauldron:StreamingResponse:TypewriterTickMilliseconds` | 15 | |
| `Cauldron:StreamingResponse:TypewriterTickChars` | 1 | |
| `Cauldron:LandingMessages` | twelve spells | Shown while Morgana loads |
| `Cauldron:TrustedImageDomains` | `raw.githubusercontent.com` | Hosts a rich card may load images from |
| `Cauldron:Widget:AllowedEmbedOrigins` | empty | Sites that may frame the widget. Empty admits nobody |

## Grimoire and Rune

| Key | Grimoire | Rune | Notes |
|---|---|---|---|
| `…:MorganaURL` | `https://localhost:5001` | same | |
| `…:CallbackURL` | `https://localhost:5004/morgana-hook` | `https://localhost:5003/morgana-hook` | Where Morgana posts messages and chunks |
| `…:Authentication:Issuer` | `grimoire` | `rune` | |
| `…:Authentication:SymmetricKey` | **required** | **required** | |
| `…:MaxInputLength` | 500 | 500 | Hard cap on the typed prompt |
| `…:MaxMessageLength` | none | 500 | The capability Rune declares |
| `…:CommandTimeoutSeconds` | 120 | 120 | |
| `…:StartupTimeoutSeconds` | 30 | 30 | |
| `…:StreamingResponse:*` | 15 ms · 1 char | not applicable | Rune does not stream |
| `…:LandingMessages` | twelve spells | twelve spells | |
