# Morgana - Multi-Agent Multi-Channel Conversational AI Framework

## What is Morgana

Morgana is a modern conversational AI framework built on **.NET 10**, **Akka.NET** (actor model) and **Microsoft.Agents.AI** (agent framework). It orchestrates specialized AI agents that collaborate to understand, classify and resolve user inquiries through intent-based routing, content moderation, shared context and tool calling.
**Cauldron** is the reference Blazor Server frontend that talks to Morgana via REST + SignalR.
**Grimoire** is the reference Spectre.Console frontend that talks to Morgana via REST + WebHook.

**Key value proposition**: domain experts model agents declaratively (prompt + tools in JSON, thin C# class), package them as plugin DLLs, and Morgana handles orchestration, streaming, persistence, guard rails, channel adaptation and observability automatically.

## Design Philosophy — agents are prose, not code

Morgana is a stable framework for *impersonating domain agents*; the framework code (Akka pipeline, tool loop, intent routing, channel adaptation) is not where day-to-day work happens. A domain agent *is* its prompt configuration — its entry in `agents.json` (Target/Instructions/Personality/Formatting + tool contracts) read together with the global policies in `morgana.json`. Building or tuning an agent is therefore ~95% authoring **clear, non-contradictory, precise prose**: every sentence is dispositive — an instruction an LLM executes, not documentation.

The characteristic defect is not an exception, it is a **logical contradiction** between two instructions read together — and these are typically **emergent and non-local**: one clause in the agent vs one in the global policies (e.g. the interplay of `#INT#` × ConversationClosure × QuickReplyEscapeOptions × ToolGrounding). Prefer structural fixes over point patches: state a unifying doctrine high in the policy order (low `Priority`, so it renders first among Critical) and let the specific policies read as instances of it — this shrinks the contradiction surface instead of chasing symptoms.

Two things *outside* the prose can still sabotage a correct prompt, and are worth ruling in/out first because they are invisible from `agents.json`:
- **Model tier** — a dense, layered prompt needs a capable model; the `Efficiency` die (e.g. Haiku) amplifies contradiction-following failures where `Performance` would not.
- **Rendering / channel code** — e.g. a rich-card leaf rendering raw `**` instead of bold was a Razor bug in Cauldron, unfixable from any prompt.

## Solution Structure

```
Morgana/
  Morgana/                 # Main solution folder (working directory)
    Morgana.Contracts/     # Zero-dependency wire contracts (NuGet package, foundation for Morgana.AI + channels)
    Morgana.AI/            # Core framework library (NuGet package)
    Morgana.Web/           # ASP.NET Core host (controllers, SignalR hub, DI wiring)
    Directory.Build.props  # Shared build settings, version, NuGet dependencies
  Channels/                # Reference channels (clients that talk to Morgana)
    Cauldron/              # Blazor Server frontend — rich-Web reference channel (SignalR)
    Grimoire/              # Spectre.Console CLI — rich-TTY reference channel (webhook)
    Rune/                  # Spectre.Console CLI — basic-TTY reference channel (webhook)
  Morgana.Examples/        # Example plugin with BillingAgent, ContractAgent, MonkeyAgent, InventoryAgent
  CHANGELOG.md
```

### Morgana.AI (core library)

| Folder | Purpose |
|---|---|
| `Abstractions/` | Base classes: `MorganaActor`, `MorganaAgent`, `MorganaLLM`, `MorganaTool` |
| `Actors/` | Pipeline actors: `ConversationManagerActor`, `ConversationSupervisorActor`, `GuardActor`, `ClassifierActor`, `RouterActor` |
| `Adapters/` | `MorganaAgentAdapter` (agent builder), `MorganaToolAdapter` (tool→AIFunction), `MCPToolAdapter` (MCP→native), `MorganaChannelAdapter` (rich→plain degradation) |
| `Attributes/` | `[HandlesIntent]`, `[ProvidesToolForIntent]`, `[UsesMCPServer]` |
| `Extensions/` | `ActorSystemExtensions` — C# 14 `extension(ActorSystem)` syntax for `GetOrCreateActorAsync<T>` and `GetOrCreateAgentAsync(Type)` |
| `Interfaces/` | All service contracts (see Service layer below) |
| `Providers/` | `MorganaAIContextProvider` (per-agent context variables, with cross-agent shared variables persisted in the conversation-scoped `shared_context` registry), `MorganaChatHistoryProvider` (chat history with optional summarizing reducer) |
| `Services/` | Default implementations of all interfaces |
| `Telemetry/` | `MorganaTelemetry` (ActivitySource, metrics), `OpenTelemetryExtensions` |
| `Records.cs` | All immutable record types (DTOs) for actor messages, configuration |
| `morgana.json` | Framework-level prompts (Morgana, Classifier, Guard, Presentation, ChannelAdapter) with global policies, base tools, error answers |

### Morgana.Web (host)

| File | Purpose |
|---|---|
| `Program.cs` | Full DI wiring: LLM provider factory, Akka.NET, SignalR, CORS, OpenTelemetry, plugin loading, all service registrations |
| `Controllers/MorganaController.cs` | REST API at `api/morgana` — conversation start/end/resume/history/message/health |
| `Hubs/MorganaHub.cs` | SignalR hub at `/morganaHub` — group-based real-time messaging |
| `Services/PluginLoaderService.cs` | Scans `plugins/` directory for DLLs containing `MorganaAgent` subclasses |
| `Services/SignalRChannelService.cs` | `IChannelService` implementation — pushes `ChannelMessage` and stream chunks over SignalR |
| `appsettings.json` | All configuration sections (see Key Configuration below) |

### Cauldron (rich reference channel)

Blazor Server app at `Channels/Cauldron/` (separate solution, has its own `CLAUDE.md`). Reference channel for Morgana: rich chat UI with streaming, quick replies, rich cards, typing indicators, conversation resume via `ProtectedLocalStorage`. Communicates via REST + SignalR (`deliveryMode=signalr`). Self-issues JWT tokens for authentication (`iss=cauldron`). Consumes the shared wire DTOs from `Morgana.Contracts` via project reference (no DTO duplication).

### Grimoire (rich reference channel — TTY)

Spectre.Console CLI at `Channels/Grimoire/` (separate solution, has its own `CLAUDE.md`). Third reference channel, sibling of Cauldron and Rune: it completes the channel × capability matrix by occupying the rich-TTY quadrant ("Rune with steroids" — not a fork of Rune, a sibling with its own life). Same stack as Rune (Kestrel-hosted console, `deliveryMode=webhook`, port 5004) but declares the **full** capability profile (all rich features on, `MaxMessageLength=null`), so it renders Morgana's non-degraded rich output natively in the terminal: Markdig→Spectre markdown rendering, hand-drawn bordered rich cards, per-turn `AnsiConsole.Live` streaming with a typewriter effect, blocking arrow-key quick replies, and scrollback. Because it advertises `MaxMessageLength=null` the `MorganaChannelAdapter` always short-circuits (hot path) — Grimoire never exercises the degradation codepath, which remains Rune's job. Self-issues JWT tokens for authentication (`iss=grimoire`). Consumes the shared wire DTOs from `Morgana.Contracts` via project reference (no DTO duplication); `StreamChunkRequest`, the webhook chunk body, now lives in `Morgana.Contracts` too.

### Rune (poor-but-honest reference channel)

Spectre.Console CLI at `Channels/Rune/` (separate solution, has its own `CLAUDE.md`). Second reference channel, complementary to Cauldron: Kestrel-hosted console app that exercises the webhook delivery path (`deliveryMode=webhook`) and the "poor but honest" capability profile (all rich features off, `MaxMessageLength=500`) — the contract surface `MorganaChannelAdapter` is supposed to degrade toward. Self-issues JWT tokens for authentication (`iss=rune`). Consumes the shared wire DTOs from `Morgana.Contracts` via project reference (no DTO duplication).

### Morgana.Examples (plugin)

Four example agents packaged as a plugin DLL (copied to `plugins/` after build). References `Morgana.AI` via **project reference** (`..\Morgana\Morgana.AI`, bringing `Morgana.Contracts` transitively) — not the NuGet package — so the in-repo/Docker build is deterministic; an out-of-tree plugin would instead reference the `Morgana.AI` NuGet package:
- `BillingAgent` + `BillingTool` — telecom billing with invoices, payment history
- `ContractAgent` + `ContractTool` — contract summarization
- `MonkeyAgent` — MCP-only agent using external MonkeyMCP server (no native tool)
- `InventoryAgent` + `InventoryTool` — greenhouse/nursery inventory; the first *dispositive* (stateful) example agent: create/confirm/cancel orders, backed by its own standalone SQLite database
- `agents.json` — embedded resource with intents and per-agent prompts/tool definitions

## Architecture and Message Flow

### Actor Hierarchy (per conversation)

```
ConversationManagerActor          ← entry point, lifecycle, channel metadata persistence
  └── ConversationSupervisorActor ← FSM orchestrator (5 states)
        ├── GuardActor            ← content moderation (IGuardRailService)
        ├── ClassifierActor       ← intent classification (IClassifierService)
        └── RouterActor           ← intent→agent routing
              ├── BillingAgent
              ├── ContractAgent
              └── MonkeyAgent
```

### REST API (MorganaController)

| Endpoint | Method | Purpose | Returns |
|---|---|---|---|
| `conversation/start` | POST | Creates conversation, validates `ChannelMetadata` (required), creates manager actor | 202 |
| `conversation/{id}/end` | POST | Terminates conversation, stops supervisor | 200 |
| `conversation/{id}/resume` | POST | Checks `ConversationExists` (404 if not), restores active agent | 202 + activeAgent |
| `conversation/{id}/message` | POST | Auth check → rate limit check (429) → dust budget check (429) → captures OTel context → tells `UserMessage` | 202 |
| `conversation/{id}/history` | GET | Returns `MorganaChatMessage[]` via persistence service | 200/404 |
| `health` | GET | Actor system liveness check | 200/503 |

All endpoints authenticate via `AuthenticateRequestAsync` (Bearer JWT validation, fail-closed).

### Turn Pipeline (FSM states in ConversationSupervisorActor)

1. **Idle** — waits for `UserMessage`
2. **AwaitingGuardCheck** — `GuardActor` checks compliance (two-level: profanity + LLM policy). Fail → rejection message, stay idle. Pass → next step
3. **AwaitingClassification** — `ClassifierActor` classifies intent (LLM-based). On failure, falls back to `"other"` intent. *Skipped if an active agent exists (follow-up flow)*
4. **AwaitingAgentResponse / AwaitingFollowUpResponse** — `RouterActor` dispatches to the target `MorganaAgent`. Agent runs LLM with tools, streams chunks back, sends final `AgentResponse`
5. Back to **Idle** — response forwarded via `ConversationManagerActor` → `IChannelService` → SignalR → Cauldron

### Multi-turn: active agent tracking

When an agent signals `IsCompleted = false` (detected via `#INT#` token, trailing question mark, quick replies, or rich card), the supervisor remembers it as `activeAgent`. Subsequent messages skip classification and go directly to that agent (after guard check). The agent signals `IsCompleted = true` when done.

### Inter-agent shared context

Tools with `Shared: true` parameters route their values into a conversation-scoped `shared_context` registry persisted alongside the agent sessions in the per-conversation SQLite DB. Writes go through `IConversationPersistenceService.UpsertSharedVariableAsync` (first-write-wins enforced via `INSERT OR IGNORE`); every agent calls `LoadSharedVariablesAsync` at the start of each turn and merges the result into its own `MorganaAIContextProvider` (first-write-wins again on the local merge: existing local values are not overwritten). Example: `userId` set by BillingAgent is available to ContractAgent without re-asking, even if Contract is activated for the first time after Billing's actor has been decommissioned.

## Service Layer

### Core Services (Morgana.AI/Services)

| Service | Interface | Purpose |
|---|---|---|
| `LLMClassifierService` | `IClassifierService` | LLM-based intent classification with formatted intents from `agents.json`. Falls back to `"other"` with confidence 0.0 on any error |
| `LLMGuardRailService` | `IGuardRailService` | Two-level moderation: (1) fast sync profanity scan from `ProfanityTerms` list, (2) async LLM policy check. Fails open on LLM error |
| `LLMPresenterService` | `IPresenterService` | LLM-generated welcome message + quick replies. Falls back to `FallbackMessage` + intent-derived buttons on LLM failure. Never throws |
| `ConfigurationPromptResolverService` | `IPromptResolverService` | Two-tier resolution: framework prompts from `morgana.json` (embedded in Morgana.AI) + domain prompts from `agents.json` (via `IAgentConfigurationService`). Case-insensitive lookup |
| `EmbeddedAgentConfigurationService` | `IAgentConfigurationService` | Scans all loaded assemblies for `agents.json` embedded resources. Graceful degradation if none found (agentless mode) |
| `HandlesIntentAgentRegistryService` | `IAgentRegistryService` | Discovers agents via `[HandlesIntent]` reflection scanning. Bidirectional validation: every configured intent must have an agent and vice versa. Throws on mismatch at startup. Delegates LLM tier validation to `ILLMTierValidationService` |
| `RequiresLLMTierValidationService` | `ILLMTierValidationService` | Validates every discovered agent's `[RequiresLLMTier]` declaration against the active provider's configured tiers. Startup-fatal on missing attribute or unconfigured tier |
| `ProvidesToolForIntentRegistryService` | `IToolRegistryService` | Discovers tools via `[ProvidesToolForIntent]` scanning. Diagnostic console output with validation warnings for orphaned tools/agents |
| `MCPClientRegistryService` | `IMCPClientRegistryService` | MCP client connection pool: keyed by URI (Http) or `stdio:{command}` (Stdio). Thread-safe via `ConcurrentDictionary`. Contains `MCPClient` wrapper over `McpClient` from ModelContextProtocol.Core |
| `SQLiteConversationPersistenceService` | `IConversationPersistenceService` | Per-conversation SQLite DB (`morgana-{id}.db`). AES-256-CBC encrypted `AgentSession` BLOBs + `shared_context` registry (first-write-wins). Schema v4: tables `morgana` + `rate_limit_log` + `channel_metadata` + `shared_context`. Manages `UpsertSharedVariableAsync` and `LoadSharedVariablesAsync` for cross-agent context synchronization |
| `SQLiteRateLimitService` | `IRateLimitService` | Sliding window algorithm (per-minute/hour/day) in same SQLite DB. Fails open on error. Delegates DB init to persistence service |
| `SQLiteDustLimitService` | `IDustLimitService` | Per-conversation token budget enforcement. Tracks cumulative dust consumed (tokens weighted by I/O asymmetry and cache tiers). Three thresholds: 70% warning (one-shot), 90% warning (one-shot), 100% lockout (blocks new turns, conversation stays alive). Fails open on DB error. Emits OTel counter `morgana.dust.consumed` tagged by LLM role |
| `JWTAuthenticationService` | `IAuthenticationService` | Validates JWT tokens: HMAC-SHA256, issuer whitelist, audience, lifetime (30s clock skew). Extracts `sub`→UserId, `name`→DisplayName |
| `SummarizingChatReducerService` | *(factory, not an interface)* | Creates `SummarizingChatReducer` from `Morgana:HistoryReducer` config. Threshold (hysteresis buffer above target, default 12) plus target count (default 8) drives summarization: reduction triggers when message count > target + threshold |
| `AdaptingChannelService` | `IChannelService` | Decorator: intercepts every `SendMessageAsync`, routes through `MorganaChannelAdapter` for capability-based degradation, then dispatches to the concrete transport via `IChannelServiceFactory` |
| `ChannelMetadataStore` | `IChannelMetadataStore` | In-memory `ConcurrentDictionary<string, ChannelMetadata>` registry. Leaf singleton (no channel-service dependency) so concrete transports like `WebhookChannelService` can read per-conversation coordinates without closing a DI cycle through the decorator |

### Providers (Morgana.AI/Providers)

| Provider | Framework Base | Purpose |
|---|---|---|
| `MorganaAIContextProvider` | `AIContextProvider` | Per-session variable dictionary via `ProviderSessionState<MorganaContextState>`. Supports `GetVariable`/`SetVariable`/`DropVariable`/`MergeSharedContext`. Shared variables (declared with `Shared: true` in config) trigger `OnSharedContextUpdate` callback wired to `IConversationPersistenceService.UpsertSharedVariableAsync` for cross-agent persistence |
| `MorganaChatHistoryProvider` | `ChatHistoryProvider` | Full history in `AgentSession`, optional reduced view via `IChatReducer` for LLM context. `ProvideChatHistoryAsync` returns reduced view for LLM; `StoreChatHistoryAsync` always appends to full history. Timestamps response messages with server UTC |

## LLM Provider Abstraction

`MorganaLLM` is the base class implementing `ILLMService`. Four concrete providers:
- `Anthropic` — Claude models via `AnthropicClient`
- `AzureOpenAI` — GPT models via `AzureOpenAIClient`
- `OpenAI` — GPT models via `OpenAIClient`
- `Ollama` — local models via `OllamaApiClient`

All wrap into `Microsoft.Extensions.AI.IChatClient`. Provider selected by `Morgana:LLM:Provider` setting.

**Two-tier models (Efficiency/Performance)**: each provider configures a `Tiers` map keyed by `LLMTier` — exactly two dies, `Efficiency` and `Performance`, modeled on Intel's E-core/P-core split. `Efficiency` is the default for Morgana's own framework actors (Guard, Classifier, Presenter, ChannelAdapter) and for any agent handling routine work; `Performance` is reserved exclusively for agents whose domain author declares an existential need for deep reasoning or high expressive power — not a "nicer to have" upgrade. There is no cross-tier fallback: a local single-model deployment (Ollama being the canonical case) simply declares a single `Efficiency` entry, and any agent requiring `Performance` fails startup until a second entry is added. Each `Tiers` entry carries an `Options` object (`Records.TierConfiguration`) and its own `MagicDust` pricing. `Options` is a deliberately narrow, JSON-bindable mirror of `Microsoft.Extensions.AI.ChatOptions` — only `ModelId` (the model/deployment identifier, replacing the old top-level `Name` field) and `MaxOutputTokens` are exposed; every other `ChatOptions` field (Temperature, TopP, StopSequences, Reasoning, Tools, ...) was deliberately excluded — see `Records.TierConfiguration` remarks for the full census and rationale. The map is a JSON **object keyed by tier name** (not an array) so User Secrets/env var overrides merge per-tier instead of positionally. A `ModelId` left on its `_SECURE_OVERRIDE_`/`_FUNCTIONAL_OVERRIDE_` placeholder fails startup (`MorganaLLM.RegisterTierClient`). At runtime, `Options.ToChatOptions()` is materialized once per tier and merged field-by-field — fill-if-absent, never overriding an explicit per-call value — into every request on that tier via `TierDefaultsChatClient`, mirroring the same merge pattern `Microsoft.Agents.AI.ChatClientAgent` already uses one layer up for agent-level defaults.

Two consumption modes:
- `CompleteWithSystemPromptAsync(conversationId, systemPrompt, message)` — stateless, for guard/classifier/presenter/channel-adapter; always runs on the **cheapest configured tier**
- `GetChatClient(tier)` / `GetPricing(tier)` — per-tier `IChatClient` + dust pricing for agent use (multi-turn with tool calling); an exact tier match is required, no fallback

Startup validation: `ILLMTierValidationService` (`RequiresLLMTierValidationService`) verifies every discovered agent declares `[RequiresLLMTier]` and that the declared tier exists in the active provider's `Tiers` map. Fail-fast on mismatch.

## Agent Authoring (how to create a new agent)

1. **Define intent** in `agents.json` Intents array: Name, Description, Label, DefaultValue
2. **Define prompt** in `agents.json` Agents array: ID matching the intent name, Target, Instructions, Personality, Formatting, Tools array
3. **Create agent class** extending `MorganaAgent`, decorated with `[HandlesIntent("myintent")]` **and** `[RequiresLLMTier(LLMTier.X)]` (mandatory — declares the fixed die, `Efficiency` or `Performance`, the agent runs on; validated at startup against the active provider's `Tiers` map). Constructor calls `MorganaAgentAdapter.CreateAgent()` which returns `(AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider)`
4. **Create tool class** (optional) extending `MorganaTool`, decorated with `[ProvidesToolForIntent("myintent")]`. Method names must match tool Names in JSON. Constructor signature: `(ILogger, Func<ToolContext>)`
5. **Or use MCP** — decorate agent with `[UsesMCPServer("url")]` (Http) or `[UsesMCPServer(MCPTransport.Stdio, "cmd", args)]` for auto-discovered remote tools. Supports multiple `[UsesMCPServer]` on one agent
6. **Package as plugin DLL** — place in `plugins/` directory, `PluginLoaderService` discovers it at startup

Minimal agent (see `BillingAgent.cs`):
```csharp
[HandlesIntent("billing")]
[RequiresLLMTier(Records.LLMTier.Efficiency)]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger logger,
        MorganaAgentAdapter adapter,
        IConfiguration configuration)
        : base(conversationId, llmService, promptResolverService, persistenceService, logger, configuration)
    {
        (aiAgent, aiContextProvider, aiChatHistoryProvider) =
            adapter.CreateAgent(GetType(), conversationId, () => CurrentSession, OnSharedContextUpdate);
    }
}
```

## Tool System

Every agent gets **base tools** from `morgana.json` (`GetContextVariable`, `SetContextVariable`, `SetQuickReplies`, `SetRichCard`) plus **domain tools** from `agents.json`. Tool parameters have two scopes:
- `Scope: "context"` — retrieved from context variables; `MorganaToolAdapter` injects `ToolParameterContextGuidance` description telling the LLM to check `GetContextVariable` first
- `Scope: "request"` — obtained directly from user; `ToolParameterRequestGuidance` description injected

`MorganaToolAdapter.AddTool` validates delegate vs definition (parameter count, names, required/optional). `CreateFunction` wraps the tool method into an `AIFunction` with enriched parameter descriptions.

**MCP tools** are auto-discovered from servers declared via `[UsesMCPServer]` and bridged through `MCPToolAdapter` using DynamicMethod IL generation (`CreateTypedDelegateWithNamedParameters`) for proper parameter names/types. Static `executorCache` ensures IL-generated delegates are cached.

## Channel Abstraction (multi-channel)

`IChannelService` is the outbound channel abstraction (2 methods: `SendMessageAsync`, `SendStreamChunkAsync`). The DI pipeline:

```
SignalRChannelService, WebhookChannelService, … (concrete transports, one per deliveryMode)
  ← IChannelServiceFactory (per-conversation dispatch, populated by ChannelServiceRegistration entries)
      ← AdaptingChannelService (decorator: adapts via MorganaChannelAdapter, then dispatches)
          → registered as IChannelService

ChannelMetadataStore (leaf singleton, no channel-service dependency)
  → registered as IChannelMetadataStore, read by the decorator AND by concrete transports
    that need per-conversation addressing (e.g. WebhookChannelService → callbackUrl)
```

**Channel handshake**: at conversation start, the client must announce `ChannelMetadata`, composed of `ChannelCoordinates` (channelName + deliveryMode — identity and addressing) + `ChannelCapabilities` (feature budget). The controller gate rejects any request whose `coordinates.channelName` or `coordinates.deliveryMode` is missing or whose `deliveryMode` is not served by a registered `IChannelService`. `ConversationManagerActor` normalizes both coordinate strings (trim + lowercase), persists the record via `SaveChannelMetadataAsync`, and registers it in the in-memory `IChannelMetadataStore`.

**Capability-based adaptation** (`MorganaChannelAdapter.AdaptAsync`):
1. Short-circuits if message fits the budget (hot path for Cauldron)
2. LLM-guided rewrite (ChannelAdapter prompt) to degrade rich content to plain text
3. Template fallback (Markdig-based strip) if LLM fails. Never throws

**Streaming suppression**: suppressed upstream when the channel doesn't support it (`SupportsStreaming = false`) or when adaptation would be needed.

**Cauldron's self-declaration**: `ChannelMetadata.Cauldron` singleton — coordinates `{ channelName: "cauldron", deliveryMode: "signalr" }`, all capabilities true, no max length.

## Prompt Architecture

Two-layer prompt composition in `MorganaAgentAdapter.ComposeAgentInstructions()`:
1. **Framework layer** (from `morgana.json`): Target + Personality + GlobalPolicies + Instructions + Formatting
2. **Domain layer** (from `agents.json`): Target + Personality + Instructions + Formatting

Framework prompts (`morgana.json`):
- **Morgana**: base personality, global policies (P0-P8 Critical: ContextHandling, QuickReplyDoctrine, InteractiveToken, ConversationClosure, SessionContinuation, ToolUsage, ToolGrounding, QuickReplyEscapeOptions, MandatoryTextualResponse; P0-P3 Operational: ToolParameterContextGuidance, ToolParameterRequestGuidance, RichCardUsage, RichCardAndQuickRepliesCombined). The `QuickReplyDoctrine` (P1) is the unifying master rule the other quick-reply policies instantiate — see Design Philosophy
- **Classifier**: JSON response `{intent, confidence}` with `((formattedIntents))` placeholder
- **Guard**: JSON response `{compliant, violation}` with ProfanityTerms list
- **Presentation**: JSON intro message with quickReplies, FallbackMessage, NoAgentsMessage
- **ChannelAdapter**: rewrites rich messages for limited channels (richCards→prose, quickReplies→inline, markdown strip, maxMessageLength)

Resolution: `ConfigurationPromptResolverService` merges morgana.json + agents.json. Case-insensitive ID matching. Domain prompts override framework prompts if same ID exists.

## Persistence

`SQLiteConversationPersistenceService` — per-conversation SQLite databases at `{StoragePath}/morgana-{conversationId}.db`:

| Table | Schema | Purpose |
|---|---|---|
| `morgana` | `agent_identifier` PK, `agent_name` UNIQUE, `agent_session` BLOB (AES-256-CBC encrypted), `creation_date`, `last_update`, `is_active` | Agent session storage (per-agent, isolated) |
| `rate_limit_log` | `request_timestamp` TEXT | Sliding window rate limiting |
| `channel_metadata` | `id` (=1), `channel_name`, `supports_rich_cards`, `supports_quick_replies`, `supports_streaming`, `supports_markdown`, `max_message_length` | Persisted channel handshake |
| `shared_context` | `variable_name` PK, `variable_value` BLOB (JSON serialized), `set_by_agent`, `creation_date` | Cross-agent shared variables (first-write-wins via `INSERT OR IGNORE`) |
| `dust_budget` | `dust_consumed` REAL, `warning_70_sent` INTEGER, `warning_90_sent` INTEGER | Lifetime token budget tracking (single row, CHECK=1 constraint on `id`) |
| `dust_usage_log` | `timestamp` TEXT, `dust_consumed` REAL, `llm_role` TEXT | Per-charge diagnostic log (optional, indexed by timestamp); enables OTel and per-role attribution |

Schema version tracked via `PRAGMA user_version` (current: 5). `EnsureDatabaseInitializedAsync` is idempotent.

History retrieval (`GetConversationHistoryAsync`): loads all agent rows → decrypts each → extracts messages from `AgentSession.stateBag.MorganaChatHistoryProvider.messages` → applies user_facing filter (skips tool-call and non-final assistant messages from marked agents) → merges chronologically → extracts quick replies and rich cards from `SetQuickReplies`/`SetRichCard` function calls.

## Authentication

JWT-based, per-issuer trust model:
- **Channel side** (e.g. Cauldron's `MorganaAuthHandler`): self-issues tokens (own `iss`, `sub`, audience `morgana.ai`, short expiry, HMAC-SHA256)
- **Morgana side**: `JWTAuthenticationService` peeks the `iss` claim, looks up the matching entry in `Morgana:Authentication:Issuers`, validates signature with that issuer's key, plus audience and lifetime (30s clock skew). Unknown issuers are rejected. Extracts `sub`→UserId, `name`→DisplayName
- **Shared key per channel**: each channel's secret matches the `SymmetricKey` of its own `Issuers[]` entry on Morgana — leaking one channel's key does not compromise the others
- **Onboarding a new channel**: any channel beyond Cauldron must be declared as an `Issuers[]` entry on the destination Morgana instance — its `Name` must equal the channel's `iss` claim, and its `SymmetricKey` must match the secret the channel uses to sign tokens. A channel whose issuer name is not declared, or whose key does not match, is rejected at the very first request (fail-closed)

## Observability

OpenTelemetry distributed tracing with per-turn spans:
```
morgana.turn → morgana.guard → morgana.classifier → morgana.router → morgana.agent
```
Attributes: `conversation.id`, `guard.compliant`, `classification.intent`, `classification.confidence`, `agent.ttft_ms`, `agent.response_preview`, `agent.is_completed`. HTTP Activity context propagated as `ActivityLink` to turn span in supervisor.

**Metrics**: `morgana.dust.consumed` counter (unit `dust`) tagged by `dust.llm_role` for per-agent/role attribution; emitted post-commit in `SQLiteDustLimitService.ChargeAsync`. Complements `gen_ai.usage.*` MEAI counters from `MorganaLLM` which break down cache tiers.

Exporters configured via `Morgana:OpenTelemetry:Exporters` array: console, OTLP (Jaeger, Grafana Tempo).

## Startup Validation

At application startup, comprehensive validation is performed:

1. **HandlesIntentAgentRegistryService**: bidirectional check — every configured intent (except `"other"`) must have a `[HandlesIntent]` agent, and every `[HandlesIntent]` agent must have a configured intent. Throws `InvalidOperationException` on mismatch
2. **RequiresLLMTierValidationService** (`ILLMTierValidationService`, delegated to by the agent registry): every discovered agent must declare `[RequiresLLMTier]`, and the declared tier must exist in the active provider's `Tiers` map. Throws on mismatch
3. **ProvidesToolForIntentRegistryService**: warns on agents without tools (MCP-only is valid), warns on orphaned tools, errors on duplicate tool registrations for same intent
4. **EmbeddedAgentConfigurationService**: warns if no `agents.json` found (agentless mode is allowed)
5. **MorganaLLM provider constructors**: reject a `Tiers` entry whose `ModelId` is still an override placeholder, or an empty `Tiers` map

## Key Configuration Sections (appsettings.json)

| Section                                             | Purpose                                                                                                                                                                                                                       |
|-----------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Morgana:LLM:Provider`                              | LLM provider: `Anthropic`, `AzureOpenAI`, `Ollama`, `OpenAI`                                                                                                                                                                  |
| `Morgana:LLM:{Provider}`                            | Provider credentials (ApiKey, Endpoint) + `Tiers` map keyed by die (`Efficiency`/`Performance`), each entry with `Options` (`ModelId`, optional `MaxOutputTokens`) and per-model `MagicDust` pricing |
| `Morgana:ActorSystem:TimeoutSeconds`                | Actor/agent receive timeout (default 180s)                                                                                                                                                                                    |
| `Morgana:ActorSystem:EnableGuardrail`               | Toggle guard rail (useful for local dev)                                                                                                                                                                                      |
| `Morgana:AdaptiveMessaging:EnableStreamingResponse` | Toggle streaming responses                                                                                                                                                                                                    |
| `Morgana:AdaptiveMessaging:RichFeaturesMinLength`   | Ingress heuristic: if the client's `MaxMessageLength` is below this threshold, `SupportsRichCards` and `SupportsQuickReplies` are forced to `false` at the handshake. Null/0 disables the heuristic. Streaming is unaffected. |
| `Morgana:ConversationPersistence`                   | StoragePath, EncryptionKey (AES-256, base64, must be 32 bytes)                                                                                                                                                                |
| `Morgana:RateLimiting`                              | Enabled, MaxMessagesPerMinute/Hour/Day, custom ErrorMessage templates with `{limit}` placeholder                                                                                                                              |
| `Morgana:DustLimiting`                              | Enabled, BudgetPerConversation (default 80 units), Warning70Message, Warning90Message, ErrorMessage. `MagicDust` pricing (InputTokensPerDustUnit, OutputTokensPerDustUnit, CachedInputWeight, CacheCreationWeight) lives per-tier on each `Tiers` entry under `Morgana:LLM:{Provider}`. Shipped defaults are calibrated on official provider pricing assuming a dual deploy of **Haiku 4.5/Sonnet 5** (Anthropic) and **gpt-4o-mini/gpt-4o** (OpenAI and AzureOpenAI), with a usability floor guaranteeing ≥10 full-length Performance turns per budget — formula in `Records.MagicDustPricing` remarks. Recalibrate when pointing a tier at a different model |
| `Morgana:Authentication`                            | Audience, Issuers[] (per-issuer Name + SymmetricKey min 256-bit)                                                                                                                                                              |
| `Morgana:HistoryReducer`                            | Enabled, SummarizationThreshold (default 12), SummarizationTargetCount (default 8), SummarizationPrompt                                                                                                                       |
| `Morgana:OpenTelemetry`                             | Enabled, ServiceName, Exporters array (name, enabled, endpoint)                                                                                                                                                               |
| `Morgana:Plugins:Directories`                       | Plugin scan directories (default: `["plugins"]`)                                                                                                                                                                              |

## Build and Run

- **Target**: .NET 10, C# latest (uses C# 14 features like `extension` blocks)
- **Build**: `dotnet build` from solution root
- **Run**: start both `Morgana.Web` (backend, default https://localhost:5001) and `Cauldron` (frontend, default https://localhost:5002)
- **Docker**: `docker compose up` starts Morgana + Cauldron (`Morgana/Morgana.Dockerfile` + `Channels/Cauldron/Cauldron.Dockerfile`); the two TTY channels Grimoire and Rune are profile-gated (`profiles: ["tui"]`) so `up` skips them, and each must be launched interactively in a separate terminal via `docker compose run --rm --service-ports --use-aliases grimoire` (or `… rune`, using `Channels/Grimoire/Grimoire.Dockerfile` / `Channels/Rune/Rune.Dockerfile`), because Spectre.Console needs to own stdin/stdout (only one TTY channel at a time). `compose run` auto-activates the service's profiles so no `--profile` flag is needed. `--use-aliases` is mandatory: `compose run` skips network aliases by default, so without it Morgana's webhook callback to `http://grimoire:5004` (resp. `http://rune:5003`) fails DNS resolution

## Conventions

- All actor messages are immutable records in `Records.cs`
- Actors use `Tell` pattern (not `Ask`) for streaming support; `Become()` for FSM state transitions
- Extension points follow the pattern: interface in `Interfaces/`, default implementation in `Services/`, DI registration in `Program.cs`
- Tool method names must match exactly the `Name` field in JSON tool definitions
- Prompts are resolved by ID matching (`"Morgana"`, `"Classifier"`, `"Guard"`, `"Presentation"`, `"ChannelAdapter"` for framework; intent name for agents)
- Rich cards use JSON polymorphic serialization with `type` discriminator (`text_block`, `key_value`, `divider`, `list`, `section`, `grid`, `badge`, `image`)
- `#INT#` token in LLM responses signals conversation continuation (agent stays active)
- Actor naming: `/user/{suffix}-{conversationId}` (e.g. `/user/supervisor-abc123`)
- Agent identifier format: `{agent_name}-{conversation_id}` (e.g. `billing-abc123`)
- Channel names normalized to lowercase at ingress
- Sensitive values in appsettings.json use `_SECURE_OVERRIDE_` placeholder — real values via User Secrets or environment variables
