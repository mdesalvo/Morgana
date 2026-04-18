# Morgana - Multi-Agent Multi-Channel Conversational AI Framework

## What is Morgana

Morgana is an advanced conversational AI framework built on **.NET 10**, **Akka.NET** (actor model) and **Microsoft.Agents.AI** (agent framework). It orchestrates specialized AI agents that collaborate to understand, classify and resolve user inquiries through intent-based routing, content moderation, shared context and tool calling. **Cauldron** is the reference Blazor Server frontend that talks to Morgana via REST + SignalR.

**Key value proposition**: domain experts model agents declaratively (prompt + tools in JSON, thin C# class), package them as plugin DLLs, and Morgana handles orchestration, streaming, persistence, guard rails, channel adaptation and observability automatically.

## Solution Structure

```
Morgana/
  Morgana/                 # Main solution folder (working directory)
    Morgana.AI/            # Core framework library (NuGet package)
    Morgana.Web/           # ASP.NET Core host (controllers, SignalR hub, DI wiring)
    Directory.Build.props  # Shared build settings, version, NuGet dependencies
  Cauldron/                # Blazor Server frontend (reference channel)
  Morgana.Examples/        # Example plugin with BillingAgent, ContractAgent, MonkeyAgent
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
| `Providers/` | `MorganaAIContextProvider` (per-agent context variables with shared-variable broadcast), `MorganaChatHistoryProvider` (chat history with optional summarizing reducer) |
| `Services/` | Default implementations of all interfaces |
| `Telemetry/` | `MorganaTelemetry` (ActivitySource, metrics), `OpenTelemetryExtensions` |
| `Records.cs` | All immutable record types (DTOs) for actor messages, configuration, rich cards, channel capabilities |
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

### Cauldron (frontend)

Blazor Server app at `Cauldron/` (separate solution, has its own `CLAUDE.md`). Reference channel for Morgana: rich chat UI with streaming, quick replies, rich cards, typing indicators, conversation resume via `ProtectedLocalStorage`. Communicates via REST + SignalR. Self-issues JWT tokens for authentication. Duplicates wire-format DTOs in `Messages/Contracts/` (no shared contracts project — sync changes in lockstep).

### Morgana.Examples (plugin)

Three example agents packaged as a plugin DLL (copied to `plugins/` after build):
- `BillingAgent` + `BillingTool` — telecom billing with invoices, payment history
- `ContractAgent` + `ContractTool` — contract summarization
- `MonkeyAgent` — MCP-only agent using external MonkeyMCP server (no native tool)
- `agents.json` — embedded resource with intents and per-agent prompts/tool definitions

## Architecture and Message Flow

### Actor Hierarchy (per conversation)

```
ConversationManagerActor          ← entry point, lifecycle, channel metadata persistence
  └── ConversationSupervisorActor ← FSM orchestrator (5 states)
        ├── GuardActor            ← content moderation (IGuardRailService)
        ├── ClassifierActor       ← intent classification (IClassifierService)
        └── RouterActor           ← intent→agent routing + context broadcast
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
| `conversation/{id}/message` | POST | Auth check → rate limit check (429) → captures OTel context → tells `UserMessage` | 202 |
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

Tools with `Shared: true` parameters trigger broadcast via `RouterActor.BroadcastContextUpdate` → all sibling agents receive `ReceiveContextUpdate` and merge values into their `MorganaAIContextProvider` (first-write-wins strategy). Example: `userId` set by BillingAgent is available to ContractAgent without re-asking.

## Service Layer

### Core Services (Morgana.AI/Services)

| Service | Interface | Purpose |
|---|---|---|
| `LLMClassifierService` | `IClassifierService` | LLM-based intent classification with formatted intents from `agents.json`. Falls back to `"other"` with confidence 0.0 on any error |
| `LLMGuardRailService` | `IGuardRailService` | Two-level moderation: (1) fast sync profanity scan from `ProfanityTerms` list, (2) async LLM policy check. Fails open on LLM error |
| `LLMPresenterService` | `IPresenterService` | LLM-generated welcome message + quick replies. Falls back to `FallbackMessage` + intent-derived buttons on LLM failure. Never throws |
| `ConfigurationPromptResolverService` | `IPromptResolverService` | Two-tier resolution: framework prompts from `morgana.json` (embedded in Morgana.AI) + domain prompts from `agents.json` (via `IAgentConfigurationService`). Case-insensitive lookup |
| `EmbeddedAgentConfigurationService` | `IAgentConfigurationService` | Scans all loaded assemblies for `agents.json` embedded resources. Graceful degradation if none found (agentless mode) |
| `HandlesIntentAgentRegistryService` | `IAgentRegistryService` | Discovers agents via `[HandlesIntent]` reflection scanning. Bidirectional validation: every configured intent must have an agent and vice versa. Throws on mismatch at startup |
| `ProvidesToolForIntentRegistryService` | `IToolRegistryService` | Discovers tools via `[ProvidesToolForIntent]` scanning. Diagnostic console output with validation warnings for orphaned tools/agents |
| `MCPClientRegistryService` | `IMCPClientRegistryService` | MCP client connection pool: keyed by URI (Http) or `stdio:{command}` (Stdio). Thread-safe via `ConcurrentDictionary`. Contains `MCPClient` wrapper over `McpClient` from ModelContextProtocol.Core |
| `SQLiteConversationPersistenceService` | `IConversationPersistenceService` | Per-conversation SQLite DB (`morgana-{id}.db`). AES-256-CBC encrypted `AgentSession` BLOBs. Schema v3: tables `morgana` + `rate_limit_log` + `channel_metadata`. History retrieval decrypts all agent sessions and merges chronologically |
| `SQLiteRateLimitService` | `IRateLimitService` | Sliding window algorithm (per-minute/hour/day) in same SQLite DB. Fails open on error. Delegates DB init to persistence service |
| `JWTAuthenticationService` | `IAuthenticationService` | Validates JWT tokens: HMAC-SHA256, issuer whitelist, audience, lifetime (30s clock skew). Extracts `sub`→UserId, `name`→DisplayName |
| `SummarizingChatReducerService` | *(factory, not an interface)* | Creates `SummarizingChatReducer` from `Morgana:HistoryReducer` config. Threshold (default 20) triggers summarization of older messages down to target count (default 8) |
| `AdaptingChannelService` | `IChannelService` + `IChannelMetadataStore` | Decorator: intercepts every `SendMessageAsync`, routes through `MorganaChannelAdapter` for capability-based degradation. Also serves as in-memory `ConcurrentDictionary<string, ChannelMetadata>` registry |

### Providers (Morgana.AI/Providers)

| Provider | Framework Base | Purpose |
|---|---|---|
| `MorganaAIContextProvider` | `AIContextProvider` | Per-session variable dictionary via `ProviderSessionState<MorganaContextState>`. Supports `GetVariable`/`SetVariable`/`DropVariable`/`MergeSharedContext`/`PropagateSharedVariables`. Shared variables trigger `OnSharedContextUpdate` callback wired to RouterActor |
| `MorganaChatHistoryProvider` | `ChatHistoryProvider` | Full history in `AgentSession`, optional reduced view via `IChatReducer` for LLM context. `ProvideChatHistoryAsync` returns reduced view for LLM; `StoreChatHistoryAsync` always appends to full history. Timestamps response messages with server UTC |

## LLM Provider Abstraction

`MorganaLLM` is the base class implementing `ILLMService`. Four concrete providers:
- `Anthropic` — Claude models via `AnthropicClient`
- `AzureOpenAI` — GPT models via `AzureOpenAIClient`
- `OpenAI` — GPT models via `OpenAIClient`
- `Ollama` — local models via `OllamaApiClient`

All wrap into `Microsoft.Extensions.AI.IChatClient`. Provider selected by `Morgana:LLM:Provider` setting.

Two consumption modes:
- `CompleteWithSystemPromptAsync(conversationId, systemPrompt, message)` — stateless, for guard/classifier/presenter/channel-adapter
- `GetChatClient()` — returns `IChatClient` for agent use (multi-turn with tool calling)

## Agent Authoring (how to create a new agent)

1. **Define intent** in `agents.json` Intents array: Name, Description, Label, DefaultValue
2. **Define prompt** in `agents.json` Agents array: ID matching the intent name, Target, Instructions, Personality, Formatting, Tools array
3. **Create agent class** extending `MorganaAgent`, decorated with `[HandlesIntent("myintent")]`. Constructor calls `MorganaAgentAdapter.CreateAgent()` which returns `(AIAgent, MorganaAIContextProvider, MorganaChatHistoryProvider)`
4. **Create tool class** (optional) extending `MorganaTool`, decorated with `[ProvidesToolForIntent("myintent")]`. Method names must match tool Names in JSON. Constructor signature: `(ILogger, Func<ToolContext>)`
5. **Or use MCP** — decorate agent with `[UsesMCPServer("url")]` (Http) or `[UsesMCPServer(MCPTransport.Stdio, "cmd", args)]` for auto-discovered remote tools. Supports multiple `[UsesMCPServer]` on one agent
6. **Package as plugin DLL** — place in `plugins/` directory, `PluginLoaderService` discovers it at startup

Minimal agent (see `BillingAgent.cs`):
```csharp
[HandlesIntent("billing")]
public class BillingAgent : MorganaAgent
{
    public BillingAgent(string conversationId, ILLMService llmService,
        IPromptResolverService promptResolverService,
        IConversationPersistenceService persistenceService,
        ILogger logger, MorganaAgentAdapter adapter,
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
SignalRChannelService (concrete transport)
  → AdaptingChannelService (decorator: adapts + stores metadata)
    → registered as IChannelService + IChannelMetadataStore
```

**Channel handshake**: at conversation start, the client must announce `ChannelMetadata` (channelName + `ChannelCapabilities`). The controller gate rejects requests without it. `ConversationManagerActor` normalizes the name to lowercase, persists it via `SaveChannelMetadataAsync`, and registers it in the in-memory `IChannelMetadataStore`.

**Capability-based adaptation** (`MorganaChannelAdapter.AdaptAsync`):
1. Short-circuits if message fits the budget (hot path for Cauldron)
2. LLM-guided rewrite (ChannelAdapter prompt) to degrade rich content to plain text
3. Template fallback (Markdig-based strip) if LLM fails. Never throws

**Streaming suppression**: suppressed upstream when the channel doesn't support it (`SupportsStreaming = false`) or when adaptation would be needed.

**Cauldron's self-declaration**: `ChannelMetadata.Cauldron` singleton — channelName `"cauldron"`, all capabilities true, no max length.

## Prompt Architecture

Two-layer prompt composition in `MorganaAgentAdapter.ComposeAgentInstructions()`:
1. **Framework layer** (from `morgana.json`): Target + Personality + GlobalPolicies + Instructions + Formatting
2. **Domain layer** (from `agents.json`): Target + Personality + Instructions + Formatting

Framework prompts (`morgana.json`):
- **Morgana**: base personality, global policies (P0-P5 Critical: ContextHandling, InteractiveToken, ConversationClosure, ToolUsage, ToolGrounding, QuickReplyEscapeOptions; P0-P3 Operational: ToolParameterContextGuidance, ToolParameterRequestGuidance, RichCardUsage, RichCardAndQuickRepliesCombined)
- **Classifier**: JSON response `{intent, confidence}` with `((formattedIntents))` placeholder
- **Guard**: JSON response `{compliant, violation}` with ProfanityTerms list
- **Presentation**: JSON intro message with quickReplies, FallbackMessage, NoAgentsMessage
- **ChannelAdapter**: rewrites rich messages for limited channels (richCards→prose, quickReplies→inline, markdown strip, maxMessageLength)

Resolution: `ConfigurationPromptResolverService` merges morgana.json + agents.json. Case-insensitive ID matching. Domain prompts override framework prompts if same ID exists.

## Persistence

`SQLiteConversationPersistenceService` — per-conversation SQLite databases at `{StoragePath}/morgana-{conversationId}.db`:

| Table | Schema | Purpose |
|---|---|---|
| `morgana` | `agent_identifier` PK, `agent_name` UNIQUE, `agent_session` BLOB (AES-256-CBC encrypted), `creation_date`, `last_update`, `is_active` | Agent session storage |
| `rate_limit_log` | `request_timestamp` TEXT | Sliding window rate limiting |
| `channel_metadata` | `id` (=1), `channel_name`, `supports_rich_cards`, `supports_quick_replies`, `supports_streaming`, `supports_markdown`, `max_message_length` | Persisted channel handshake |

Schema version tracked via `PRAGMA user_version` (current: 3). `EnsureDatabaseInitializedAsync` is idempotent.

History retrieval (`GetConversationHistoryAsync`): loads all agent rows → decrypts each → extracts messages from `AgentSession.stateBag.MorganaChatHistoryProvider.messages` → filters tool messages and summarization markers → merges chronologically → extracts quick replies and rich cards from `SetQuickReplies`/`SetRichCard` function calls.

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

Exporters configured via `Morgana:OpenTelemetry:Exporters` array: console, OTLP (Jaeger, Grafana Tempo).

## Startup Validation

At application startup, three registries perform comprehensive validation:

1. **HandlesIntentAgentRegistryService**: bidirectional check — every configured intent (except `"other"`) must have a `[HandlesIntent]` agent, and every `[HandlesIntent]` agent must have a configured intent. Throws `InvalidOperationException` on mismatch
2. **ProvidesToolForIntentRegistryService**: warns on agents without tools (MCP-only is valid), warns on orphaned tools, errors on duplicate tool registrations for same intent
3. **EmbeddedAgentConfigurationService**: warns if no `agents.json` found (agentless mode is allowed)

## Key Configuration Sections (appsettings.json)

| Section | Purpose |
|---|---|
| `Morgana:LLM:Provider` | LLM provider: `Anthropic`, `AzureOpenAI`, `Ollama`, `OpenAI` |
| `Morgana:LLM:{Provider}` | Provider-specific settings (ApiKey, Model, Endpoint, DeploymentName) |
| `Morgana:ActorSystem:TimeoutSeconds` | Actor/agent receive timeout (default 180s) |
| `Morgana:ActorSystem:EnableGuardrail` | Toggle guard rail (useful for local dev) |
| `Morgana:AdaptiveMessaging:StreamingResponse:Enabled` | Toggle streaming responses |
| `Morgana:AdaptiveMessaging:RichFeaturesMinLength` | Ingress heuristic: if the client's `MaxMessageLength` is below this threshold, `SupportsRichCards` and `SupportsQuickReplies` are forced to `false` at the handshake (SMS/IVR profile). Null/0 disables the heuristic. Streaming is unaffected |
| `Morgana:ConversationPersistence` | StoragePath, EncryptionKey (AES-256, base64, must be 32 bytes) |
| `Morgana:RateLimiting` | Enabled, MaxMessagesPerMinute/Hour/Day, custom ErrorMessage templates with `{limit}` placeholder |
| `Morgana:Authentication` | Audience, Issuers[] (per-issuer Name + SymmetricKey min 256-bit) |
| `Morgana:HistoryReducer` | Enabled, SummarizationThreshold (default 20), SummarizationTargetCount (default 8), SummarizationPrompt |
| `Morgana:OpenTelemetry` | Enabled, ServiceName, Exporters array (name, enabled, endpoint) |
| `Morgana:Plugins:Directories` | Plugin scan directories (default: `["plugins"]`) |

## Build and Run

- **Target**: .NET 10, C# latest (uses C# 14 features like `extension` blocks)
- **Build**: `dotnet build` from solution root
- **Run**: start both `Morgana.Web` (backend, default https://localhost:7042) and `Cauldron` (frontend, default https://localhost:7172)
- **Docker**: `docker-compose up` using `Morgana.Dockerfile` + `Cauldron.Dockerfile`

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
- All service registries use `Lazy<T>` initialization for thread-safe startup
- Sensitive values in appsettings.json use `_SECURE_OVERRIDE_` placeholder — real values via User Secrets or environment variables
