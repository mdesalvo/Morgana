# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [0.15.0] - UNDER DEVELOPMENT
### 🎯 Major Feature: Intelligent Context Window Management
This release introduces **automatic conversation history management** through **LLM-based summarization**, dramatically reducing token costs (**60%+ savings**) for long conversations while maintaining **complete transparency** for users and **seamless agent handoffs** through incremental summary generation.

### ✨ Added
**Context Window Management via SummarizingChatReducer**
- Built on Microsoft's default `SummarizingChatReducer` from `Microsoft.Extensions.AI`
- Automatic conversation summarization when message count exceeds configurable threshold (default: 20 messages)
- Intelligent message preservation: never splits tool call sequences, always cuts at user message boundaries
- **Incremental summarization**: new summaries incorporate previous summaries (no information drift)
- **Full transparency**: UI and persistence always show complete history (**reduction only affects LLM context**)
- Customizable summarization prompts for domain-specific data preservation (invoice numbers, customer IDs, etc.)

**Enhanced MorganaChatHistoryProvider**
- Reducer applies **before** LLM receives messages (**immediate cost savings**)
- Full history always stored in `InMemoryChatHistoryProvider` (**reducer never modifies storage**)

**Configuration Section: HistoryReducer**
```json
{
  "Morgana": {
    "HistoryReducer": {
      "Enabled": true,
      "SummarizationThreshold": 20,
      "SummarizationTargetCount": 8,
      "SummarizationPrompt": "Generate a concise summary in 3-4 sentences. ALWAYS preserve: user IDs..."
    }
  }
}
```

### 🔄 Changed
- Converted residual Akka.NET `.Ask` flows into `.Tell` pattern, eliminating temporary actors and improving guard+classifier performances

### 🐛 Fixed

### 🚀 Future Enablement
- **Production cost predictability** - 60%+ token reduction enables sustainable deployment of long-running customer service conversations without budget concerns, transforming Morgana from prototype to production-ready platform
- **Enhanced domain customization** - Custom `IChatReducer` implementations can add business-specific summarization strategies (e.g., always preserve compliance data, legal citations, or audit trails) while maintaining Microsoft's proven architecture
- **Token budget enforcement** - Foundation for implementing `ITokenBudgetService` to track cumulative token usage per user/day, enabling tiered pricing models and fine-grained cost control beyond message-based rate limiting


## [0.14.0] - 2026-02-01
### 🎯 Major Feature: Real-Time Streaming Responses
This release introduces **native streaming response delivery**, providing **immediate visual feedback** and **progressive content rendering** with generative AI typewriter effects, dramatically improving perceived responsiveness and user engagement.

### ✨ Added
**Streaming Response Architecture**
- End-to-end streaming pipeline from LLM to UI using `Microsoft.Agents.AI.RunStreamingAsync()`
- Progressive chunk delivery through Akka.NET actor system using `Tell`-based message passing
- Real-time SignalR bridge for streaming chunks: `SendStreamChunkAsync()` method
- Frontend typewriter effect (configurable speed via `appsettings.json`)
- Automatic buffer management and smooth character-by-character display

**Actor System Refactoring for Streaming**
- **MorganaAgent**: Converted from `RunAsync()` to `RunStreamingAsync()` with chunk accumulation
- **RouterActor**: Migrated from `Ask` pattern to `Tell` pattern with `streamingContexts` dictionary for chunk routing
- **ConversationSupervisorActor**: Transitioned from `Ask/PipeTo` to `Tell`-based message handling for both router and active agent flows
- **ConversationManagerActor**: Removed `Ask` pattern dependency, direct `Tell`-based communication with supervisor
- Preserved `Ask` pattern for synchronous operations (`GuardActor`, `ClassifierActor`)

**Enhanced Typing Indicator**
- Replaced bouncing dots with **animated sparkle stars** (✨ theme)
- SVG-based stars with pulse, rotation and glow effects
- Color-coded indicators:
  - **Violet stars** (primary color) for base Morgana agent
  - **Pink stars** (secondary color) for specialized agents

### 🔄 Changed

### 🐛 Fixed

### 🚀 Future Enablement
- **Token-level analytics and optimization** - Streaming architecture enables precise measurement of time-to-first-token (TTFT) and tokens-per-second (TPS) metrics, unlocking data-driven LLM provider selection and cost-per-performance optimization
- **Progressive UI enhancements** - Platform for implementing streaming citations, dynamic content formatting (code blocks, tables), and live preview rendering as structured content arrives from LLMs


## [0.13.0] - 2026-01-30
### 🎯 Major Feature: Conversation Rate Limiting Protection
This release introduces **intelligent conversation rate limiting**, protecting Morgana from excessive usage and token consumption while maintaining excellent user experience through **configurable limits**, **graceful degradation**, and **user-friendly feedback**.

### ✨ Added
**IRateLimitService**
- Abstraction for pluggable rate limiting strategies (in-memory, Redis, distributed)
- Sliding window algorithm for accurate request counting over time periods
- Support for multiple concurrent limits: per-minute, per-hour, per-day

**SQLiteRateLimitService**
- Default implementation storing rate limit state in conversation-specific SQLite databases
- Automatic schema migration: adds `rate_limit_log` table to existing conversation databases
- Sliding window implementation: counts requests in last N seconds/hours/days
- Automatic cleanup of expired records (>24 hours old) to prevent database bloat
- Delegates to `IConversationPersistenceService` for schema initialization

**Database Schema Enhancement**
```sql
CREATE TABLE rate_limit_log (
    request_timestamp TEXT NOT NULL,  -- ISO 8601 format
);
```

**Rate Limiting Configuration**
```json
"Morgana": {
  "RateLimiting": {
    "MaxMessagesPerMinute": 5,
    "MaxMessagesPerHour": 30,
    "MaxMessagesPerDay": 100,
    "ErrorMessagePerMinute": "✋ Whoa there! You're casting spells too quickly...",
    "ErrorMessagePerHour": "🕐 You've reached the hourly limit of {limit} messages...",
    "ErrorMessagePerDay": "📅 Daily limit of {limit} messages reached...",
    "ErrorMessageDefault": "⚠️ Rate limit exceeded. Please slow down."
  }
}
```

### 🔄 Changed
- Standardized failure handling across all actors using `Records.FailureContext` wrapper for consistent error routing
- Unified error and warning handling in Cauldron: All runtime errors and system warnings now use auto-dismissing `FadingMessage` component with severity-appropriate durations, replacing scattered error banner implementations
- Updated `Akka.NET` dependency to 1.5.59
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-preview.260128.1 (**BREAKING CHANGES**: `AgentThread` -> `AgentSession`)
- Updated `ModelContextProtocol.Core` dependency to 0.7.0-preview.1

### 🐛 Fixed
- Certain LLM providers (like OpenAI) generate response messages with Unix timestamps (without milliseconds component)
- Fixed dead letter issues in actor error handling by implementing unified `FailureContext` pattern to preserve sender references
- Fixed residual dead letter in `ConversationManagerActor` which still responded to conversation creation or resume

### 🚀 Future Enablement
- **Operational cost control and budget predictability** - Direct protection against uncontrolled token and API resource consumption, enabling production deployment of Morgana with predictable and sustainable costs, even with large user bases
- **Tiered monetization and premium models** - Foundation for your commercial strategies based on usage limits (e.g: freemium with basic thresholds, premium tiers with higher limits, enterprise with custom quotas), transforming rate limiting from pure cost protection into a revenue driver


## [0.12.1] - 2026-01-25
### 🎯 Major Feature: Production-Ready Docker Deployment
This release introduces **complete Docker containerization** of both **Morgana (backend)** and **Cauldron (frontend)**, enabling **single-command deployment**, **reproducible builds**, and **seamless distribution** via Docker Hub with automated CI/CD pipelines.

### ✨ Added
**Docker Multi-Stage Builds**
- `Morgana.Dockerfile`: Optimized 3-stage build (SDK → Publish → Runtime) for backend containerization
- `Cauldron.Dockerfile`: Optimized 3-stage build (SDK → Publish → Runtime) for frontend containerization
- Multi-platform support: `linux/amd64` and `linux/arm64` (Apple Silicon, Raspberry Pi)
- Layer caching optimized for faster rebuilds (dependencies cached separately from source code)
- Runtime images based on `mcr.microsoft.com/dotnet/aspnet:10.0` (~200MB final size)

**Docker Compose Orchestration**
- `docker-compose.yml`: Single-file orchestration for **Morgana + Cauldron** with dedicated bridge network
- Automatic service dependency management (Cauldron waits for Morgana startup)
- Persistent volume for SQLite conversation databases (`morgana-data`)
- Environment-based configuration via `.env` file (secrets externalized from images)

**Automated Versioning from Single Source of Truth**
- `Directory.Build.targets`: MSBuild integration for automatic `.env.versions` generation during build
- Extracts versions from `Directory.Build.props` via XmlPeek and populates `MORGANA_VERSION`/`CAULDRON_VERSION`
- Eliminates manual version management across Docker files (ARG-based propagation)
- OCI-compliant image labels with dynamic version injection (`org.opencontainers.image.version`)

**GitHub Actions CI/CD Pipeline**
- `.github/workflows/docker-publish.yml`: Fully automated build and publish workflow
- Triggered when publishing a GitHub Release (GitHub creates the version tag at publication time)
- Version extraction directly from `Directory.Build.props` (single source of truth validation)
- Multi-platform image builds with Docker Buildx
- Automated push to Docker Hub: `mdesalvo/morgana:X.Y.Z` and `mdesalvo/cauldron:X.Y.Z`
- Workflow validates successful publication and build completion

**Security & Configuration**
- Secrets externalized via environment variables (no hardcoded API keys in images)
- AES-256 encryption key generation documented (OpenSSL/PowerShell commands)
- LLM provider configuration: Anthropic Claude or Azure OpenAI (runtime-switchable)
- Network isolation: Dedicated Docker bridge network for internal service communication

### 🔄 Changed

### 🐛 Fixed

### 🚀 Future Enablement
This release unlocks:
- **Docker Hub distribution**: Public images available at `mdesalvo/morgana` and `mdesalvo/cauldron`
- **Cloud deployment readiness**: Azure Container Instances, AWS ECS, Google Cloud Run
- **CI/CD integration**: Automated testing, security scanning, and deployment pipelines


## [0.11.0] - 2026-01-24
### 🎯 Major Feature: Multi-Agent Conversation History
This release introduces **virtual unified conversation timeline**, enabling **Cauldron** to display the **complete chronological message flow across all agents** by reconciling agent-isolated storage at startup.

### ✨ Added
**IConversationHistoryService**
- HTTP-based abstraction for retrieving complete conversation history from Morgana
- Designed to work seamlessly with `ProtectedLocalStorage` conversation resume flow

**MorganaConversationHistoryService**
- Default implementation calling `GET /api/conversation/{conversationId}/history` Morgana endpoint
- Synchronous HTTP retrieval (not SignalR) to ensure complete history loads before UI initialization
- Graceful error handling: returns `null` on 404 (conversation not found) or network errors
- Enables magical loader to remain active until history is fully populated

**ConversationController: GetConversationHistory Endpoint**
- New REST endpoint: `GET /api/conversation/{conversationId}/history`
- Returns chronologically ordered messages from all agents
- Delegates to `IConversationPersistenceService.GetConversationHistoryAsync()` for data retrieval

**SqliteConversationPersistenceService: History Reconciliation**
- `GetConversationHistoryAsync(conversationId)` method for cross-agent message reconstruction
- Agent-isolated storage → Virtual unified conversation timeline
- Decrypts and deserializes `AgentThread` BLOBs from each agent's SQLite row
- Extracts `ChatMessage[]` from `AgentThread` JSON structure
- Preserves agent metadata (`agentName`, `agentCompleted`) for UI context

**Cauldron: Conversation Resume Flow**
- `Index.razor` enhanced with automatic history loading on conversation resume
- **Resume Flow**:
  1. Detect saved `conversationId` in `ProtectedLocalStorage` (on `OnAfterRenderAsync`)
  2. Call `POST /api/conversation/{conversationId}/resume` to restore backend actor hierarchy
  3. Join SignalR group for real-time message delivery
  4. Call `IConversationHistoryService.GetHistoryAsync()` to fetch complete message history
  5. Populate `chatMessages` list and remove magical sparkle loader
- **Fallback handling**: If resume fails (404/500), clears storage and starts new conversation
- **Presentation message injection**: Synthetic presentation message prepended when history exists (for visual consistency)

### 🔄 Changed
- Updated `Microsoft.Agents.AI` dependency to **1.0.0-preview.260121.1**
- Enhanced `MorganaAIContextProvider` to handle context data as **thread-safe** and **immutable** collections
- Optimized `ConversationController` to replace `Ask<T>` with `Tell` fire-and-forget
- Introduced SignalR data contract between Morgana and Cauldron for better maintainability

### 🐛 Fixed
- User messages were sent to the agent's thread without timestamp
- Concurrent agent responses were displayed out of order due to processing time differences

### 🚀 Future Enablement
This release unlocks:
- **Cross-session continuity** for users returning to active conversations
- **Multi-device sync** potential (history accessible from any client with valid `conversationId`)


## [0.10.0] - 2026-01-21
### 🎯 Major Feature: Conversation Persistence
This release introduces **persistent conversation storage**, enabling Morgana to resume conversations across application restarts while maintaining full context and message history.

### ✨ Added
**IConversationPersistenceService**
- Abstraction for pluggable persistence strategies (SQLite, PostgreSQL, SQL Server, etc.)
- `SaveConversationAsync()` and `LoadConversationAsync()` for **AgentThread serialization/deserialization**
- Full integration with **Microsoft.Agents.AI** framework for automatic state management (context + messages)

**SqliteConversationPersistenceService**
- Default implementation storing conversations in **SQLite** databases
- One database per conversation: `morgana-{conversationId}.db`
- Table schema with agent-specific rows containing encrypted AgentThread BLOBs
- **AES-256-CBC encryption** with IV prepended to ciphertext for data-at-rest protection
- Optimized for single-writer scenario (one active agent per conversation)

**Database Schema**
```sql
CREATE TABLE morgana (
    agent_identifier TEXT PRIMARY KEY,     -- e.g., "billing-conv12345"
    agent_name TEXT UNIQUE,                -- e.g., "billing"
    conversation_id TEXT,                  -- e.g., "conv12345"
    agent_thread BLOB,                     -- AES-256 encrypted AgentThread's JSON
    creation_date TEXT,                    -- ISO 8601 timestamp (immutable)
    last_update TEXT                       -- ISO 8601 timestamp (updated on save)
    is_active                              -- INTEGER 0/1
);
```

**MorganaAgent Persistence Integration**
- Automatic thread loading on first agent invocation via `LoadConversationAsync()`
- Automatic thread saving after each turn via `SaveConversationAsync()`
- Seamless serialization of both **ChatMessageStore** (conversation history) and **AIContextProvider** (context variables)
- Each agent maintains isolated thread storage per conversation

**Cauldron: ProtectedLocalStorage and UI/UX continuity**
- Cauldron stores the conversation identifier in ASP.NET `ProtectedLocalStorage`, which keeps it encrypted and always available across client/server restarts
- It automatically detects the **last active agent** of the conversation, then properly recontextualizes the UI and sends an agent-resuming user message
- If there was no active agent (Morgana had the control) it just recontextualizes the UI
- Cauldron has a new button for starting a fresh new conversation with Morgana: this offers a confirmation modal, which is a new capability

### 🔄 Changed
- `ConversationId` is not provided anymore to agent's `ChatOptions`, since we moved to `ChatMessageStore`
- AgentName is now contextualized to color scheme of the current agent for better usabilty (instead of white)
- Status of SignalR connection is now green or red for better usabilty (instead of white)
- Refactored RouterActor from eager to lazy agent creation (Akka.NET best practice for hierarchical actor systems)

### 🐛 Fixed

### 🚀 Future Enablement
This release unlocks:
- **Enterprise-grade persistence** with any server or cloud solution by implementing custom `IConversationPersistenceService`
- Conversation analytics and auditing through database queries


## [0.9.0] - 2026-01-14
### 🎯 Major Feature: ConversationClosure Policy
This release introduces a new critical global policy **ConversationClosure** exploiting quick replies to give the user **explicit control** over whether to **continue** with the current agent or **return** to Morgana.
### 🎯 Major Feature: QuickReplyEscapeOptions Policy
This release introduces a new critical global policy **QuickReplyEscapeOptions** enriching quick replies coming from tools with 2 additional options letting the user **decide** whether to **continue** with the current agent by asking something else or **return** to Morgana.
### 🎯 Major Feature: ToolGrounding Policy
This release introduces a new critical global policy **ToolGrounding** enforcing quick replies emission rules to be **tied to effective agent's capabilities**.

### ✨ Added
**ConversationClosure**
- When LLM decides to not emit #INT# token for conversation continuation, it is now instructed to generate a **soft-continuation set of quick replies** engaging the user in the choice of **staying** in the active conversation with the agent or **exiting** to Morgana. This should significantly drop occurrence of unexpected agent exits.
  
**QuickReplyEscapeOptions**
- When LLM generates quick replies coming from tool's analysis, it is now instructed to include 2 additional entries to give the user the chance to **continue** the active conversation with the agent by asking something more or **returning** back to Morgana. The last one has a primary color scheme indicating Morgana. This should enhance usability of quick replies by offering an early exit-strategy to change the active agent.

**ToolGrounding**
- When LLM generates quick replies coming from tool's analysis, it is now instructed to **not invent capabilities or support paths** which are not expressely encoded in the tools. This should reduce the surface of AI hallucinations which could lead before to unpredictable conversation paths.

### 🔄 Changed
- Supervisor now works more strictly with Guard, ensuring every user message is checked for language safety and policy compliance
- Better integration with Microsoft.Agents.AI by correctly providing `AIContextProviderFactory` to the `AIAgent` constructor
- README has been slightly enhanced by replacing the ASCII diagram with more polite `mermaid` flow charts 

### 🐛 Fixed
- `Index.razor` was not rendering quick replies via `QuickReplyButton` component
- Global policy `InteractiveToken` should have Type="Critical"
  
### 🚀 Future Enablement
This release unlocks:
- `AIContextProvider` hooks can now be exploited for accessing `AIContext` **before and after LLM roundtrips**
- Termination of an agent's conversation can now be given a custom LLM-driven behavior (e.g: triggering a NPS)
- Morgana has become a **language-safe and policy-compliant** conversational environment


## [0.8.2] - 2026-01-12

### 🐛 Fixed
- FSM behavior of supervisor caused unregistration of timeout handler, leading to dead-letters at timeout
- Typing bubble color was always tied to color scheme of "Morgana" agent
- Textarea border color was always tied to color scheme of "Morgana" agent
- Send button color was always tied to color scheme of "Morgana" agent


## [0.8.1] - 2026-01-11

### 🐛 Fixed
- Morgana tools (`GetContextVariable`, `SetContextVariable`, `SetQuickReplies`) were not injected into MCP-only agents

### 🚀 Future Enablement
This release unlocks:
- MCP-only agents can now express quick-replies and access to the context like Morgana agents


## [0.8.0] - 2026-01-10
### 🎯 Major Feature: Model Context Protocol (MCP) Integration
This release introduces **industrial-grade MCP support**, enabling agents to dynamically acquire capabilities from external MCP servers without code changes. Built on **Microsoft's official ModelContextProtocol library**, Morgana treats MCP tools as first-class citizens—indistinguishable from native tools.

### ✨ Highlights
**MCP Server Integration**
- `UsesMCPServersAttribute` for declarative MCP server dependencies on agents
- `IMCPClientRegistryService` and `MCPClientRegistryService` for managing multiple MCP server connections
- `MCPClient` with HTTP/SSE transport supporting `tools/list` and `tools/call` operations
- `MCPToolAdapter` for automatic JSON Schema → ToolDefinition conversion with type safety
- Automatic tool discovery and registration at agent creation time
- MCP server configuration via `appsettings.json` under `MCP:Servers` section

**Type-Safe Parameter Handling**
- JSON Schema type mapping to CLR types: `string`, `integer` → `int`, `number` → `double`, `boolean` → `bool`
- **DynamicMethod IL generation** using Reflection.Emit for parameter name preservation (required by `AIFunctionFactory`)
- Mixed-type parameter support with automatic boxing for value types
- Object array executor pattern enabling unlimited parameter counts
- Type conversion layer ensuring JSON serialization compatibility with MCP servers

**MonkeyAgent Example (Morgana.AI.Examples)**
- Educational MCP integration example using MonkeyMCP server from Microsoft
- 5 automatically acquired tools: `get_monkeys`, `get_monkey(name)`, `get_monkey_journey(name)`, `get_all_monkey_journeys`, `get_monkey_business` 🐵
- Demonstrates transparent MCP tool usage alongside native tools

**AgentAdapter Enhancement**
- Automatic MCP tool registration during agent initialization via `RegisterMCPToolsFromServer()`
- Multi-server support per agent through `UsesMCPServers` attribute array
- Seamless integration with existing tool registration pipeline

### 🔄 Changed
- Migrated solution files to **slnx** format
- Centralized project definition via **Directory.Build.Props** standard
- Reorganized solution into **4 framework projects** (Morgana.Startup, Morgana.Foundations, Morgana.Actors, Morgana.Agents) plus **1 didactic bonus** (Morgana.Example)

### 🐛 Fixed

### 🚀 Future Enablement
This release unlocks:
- Microservices exposing tools via MCP for shared agent capabilities
- Third-party MCP tool ecosystem integration (filesystem, database, API connectors)


## [0.7.1] - 2026-01-09

### 🔄 Changed
**Dynamic Welcome**
- Morgana now welcomes with a dynamic configurable landing message (`Morgana:LandingMessages`)

### 🐛 Fixed
- Typing message was always tied to color scheme of "Morgana" agent
- Textarea did not contextualize to the active agent name


## [0.7.0] - 2026-01-07
### 🎯 Major Feature: LLM-Driven Quick Replies System
This release introduces a sophisticated **Quick Replies system** that enables LLMs to dynamically create interactive button options for users, significantly improving UX/UI for multi-choice scenarios and guided conversations.

### ✨ Added
**SetQuickReplies System Tool**
- LLM-callable system tool `SetQuickReplies` for creating interactive button options dynamically during conversations
- Supports JSON array input with `id`, `label` (emoji-enhanced display text), and `value` (message sent on click)
- Automatic storage in `MorganaContextProvider` using private context key `__pending_quick_replies`
- `MorganaAgent.GetQuickRepliesFromContext()` method for retrieving LLM-generated quick replies from context
- JSON deserialization from context string storage to `List<QuickReply>` objects
- Enhanced `AgentResponse`, `ActiveAgentResponse`, and `ConversationResponse` to propagate quick replies through actor pipeline

**Completion Logic Enhancement**
- Multi-heuristic agent completion detection: `!hasInteractiveToken && !endsWithQuestion && !hasQuickReplies`
- Agents remain active when offering quick replies to handle button clicks via follow-up flow
- Prevents mis-classification of quick reply clicks as "other" intent when agent prematurely completes

**Example Agents Enhancement (Morgana.AI.Examples)**
- **BillingAgent**: Quick replies for invoice selection, payment history navigation
- **ContractAgent**: Quick replies for clause selection, termination confirmation (Yes/No)
- **TroubleshootingAgent**: Quick replies for diagnostic guide selection (No Internet, Slow Speed, WiFi Issues)
- QUICK REPLY USAGE instructions added to all agent prompts in `agents.json`
- Formatting guidance to prevent excessive markdown and ASCII separators in responses

**Internationalization**
- Complete English localization of Morgana/Cauldron codebase and configuration files
- Removed all Italian language residuals from prompts, logs, and error messages
- Unified language consistency across `morgana.json` and `agents.json`

### 🔄 Changed
**Context Variable Management**
- Introduced `DropVariable(variableName)` method in `MorganaContextProvider` for explicit temporary variable cleanup

### 🐛 Fixed
- Quick replies lost during follow-up flow due to missing parameter in `ConversationResponse` creation

### 🚀 Future Enablement
This release unlocks:
- Dynamic conversation guidance through LLM-generated interactive options
- Improved UX for multi-step workflows (invoice selection, troubleshooting guides, contract clauses)
- Foundation for more sophisticated UI interactions (carousels, cards, forms)


## [0.6.0] - 2026-01-04
### 🎯 Major Feature: Morgana as agnostic conversational AI framework
This release represents a fundamental shift in enterprise readyness: **Morgana is now fully decoupled from any domain-specific agents**, becoming a true **conversational AI framework**.

### ✨ Added
**Plugin System**
- Morgana dynamically loads domain assemblies configured in `appsettings.json` under `Plugins:Assemblies`. At bootstrap, `PluginLoaderService` validates that each assembly contains at least one class extending `MorganaAgent`, otherwise it's skipped with a warning. This enables **complete decoupling between framework (Morgana.AI) and application domains (e.g., Morgana.AI.Examples)**, while maintaining automatic discovery of agents and tools via reflection.

### 🔄 Changed
- Improved visual cues for the active agent: name displayed in the header with a distinctive color scheme (purple for basic Morgana, pink for specialized agents).
- Extracted example agents, tools and servers into a new, separate "educational" project `Morgana.AI.Examples` to keep the core framework clean and reusable.

### 🐛 Fixed
- Morgana presents with invented quick-replies when no intent is available from the domain

### 🚀 Future Enablement
This release unlocks:
- Custom UI themes/skins with dynamic branding
- Platform extensibility via plugin system


## [0.5.0] - 2026-01-01
### 🎯 Major Feature: Proactive Conversational Paradigm
This release represents a fundamental shift in user interaction: **Morgana now initiates conversations** rather than waiting passively for user input. She automatically presents herself with capabilities aligned to classified intents, creating a more engaging and guided experience.

### ✨ Added
**Proactive Presentation System**
- Automatic presentation generation triggered by `ConversationManagerActor` on conversation creation
- LLM-driven presentation message with dynamic quick reply buttons
- Structured `IntentDefinition` configuration with `Label` and `DefaultValue` for UI rendering
- Fallback mechanism: LLM-generated presentation → prompts.json fallback message on error

**Quick Reply Interactive System**
- Client-side quick reply button rendering with emoji-enhanced labels
- Click-to-send workflow: button selection → visual feedback → automatic message submission
- State management: buttons disabled after selection with visual confirmation (checkmark)
- `SelectedQuickReplyId` tracking in `ChatMessage` for UI state persistence
- Textarea and send button disabled when quick replies are active
- Animated slide-in presentation with staggered button appearance

**Structured Message Protocol**
- `SendStructuredMessageAsync()` in `ISignalRBridgeService` supporting `messageType` and `quickReplies`
- `MessageType` enum: `User`, `Assistant`, `Presentation`, `Error`
- `StructuredMessage` record extending basic message with metadata
- SignalR message format enhanced with `messageType` and `quickReplies` array

**Configuration-Driven Presentation**
- `Presentation` prompt in prompts.json with system instructions for LLM generation
- Intent-to-capability mapping through declarative `IntentDefinition.Label`/`DefaultValue`
- `FallbackMessage` configuration for error scenarios
- Separation of displayable intents (user-facing) vs. classification intents (backend)

### 🔄 Changed
**CSS Modularization**
- Split monolithic `site.css` into modular components (`site.css`, `cauldron.css`, `quick-reply.css`, `sparkle-loader.css`) for improved maintainability
- Introduced welcome animation with magical sparkle effect featuring purple-white gradient core, orbiting particles, and expanding rings

**Actor Flow Modifications**
- `ConversationManagerActor.HandleCreateConversationAsync()` now triggers `GeneratePresentationMessage`
- `ConversationSupervisorActor` enhanced with presentation generation and handling states

**Prompt Structure Enhancements**
- `IntentDefinition` record now includes `Label` (UI display) and `DefaultValue` (button value)
- prompts.json `Classifier.Intents` array supports full intent metadata
- Intent configuration serves dual purpose: classification (backend) + presentation (frontend)

### 🐛 Fixed
**Blazor Server Render Mode Issue**
- Fixed double SignalR connection caused by `InteractiveServer` render-mode in `App.razor`
  - Root cause: Blazor pre-rendered component on server, then re-initialized on client, creating two parallel conversations
  - Symptom: Two `ConversationId` instances, first disconnected immediately after LLM presentation call
  - Impact: Wasted LLM tokens, orphaned actor hierarchies (ConversationManagerActor → ConversationSupervisorActor → Guard/Classifier/Router)
  - Solution: Changed to `Server` render-mode (non-interactive) ensuring single conversation lifecycle
  - Result: Eliminated duplicate initialization, reduced memory footprint, prevented race conditions

### 🚀 Future Enablement
This release unlocks:
- **Proactive paradigm**: Personalized greetings, context-aware suggestions, guided onboarding, A/B testing, analytics


## [0.4.0] - 2025-12-28
### 🎯 Major Refactoring: Actor Model Best Practices
This release represents a fundamental architectural improvement, transforming Morgana from an "ASP.NET with actors on top" into a **production-ready actor-based system** fully aligned with Akka.NET best practices.

### ✨ Added
**Layered Personality System**
- Two-tier personality architecture: global (Morgana) + agent-specific personalities
- Subordination principle: agent personalities complement, never contradict global traits
- Domain-appropriate behavior while maintaining brand coherence

**Global Policies Framework**
- Centralized policy management with `GlobalPolicy` record (`Name`, `Description`, `Type`)
- Policy types: Critical (inviolable rules) and Operational (procedural guidelines)
- Automatic injection into all agent instructions
- Tool parameter guidance policies: `ToolParameterContextGuidance` and `ToolParameterRequestGuidance` applied based on parameter `Scope`

**Actor Base Classes**
- `MorganaActor`: Base for all Akka.NET actors
  - Built-in `ILoggingAdapter logger` for infrastructure logging
  - Default 60-second receive timeout with virtual `HandleReceiveTimeout`
- `MorganaAgent`: Specialized base for AI agents
  - Dual-level logging: `logger` (Akka) + `agentLogger` (ILogger<T>)
  - Inherits timeout and infrastructure logging from `MorganaActor`

**State Machine Behaviors (ConversationSupervisorActor)**
- Explicit state machine with 5 behaviors: `Idle`, `AwaitingGuardCheck`, `AwaitingClassification`, `AwaitingAgentResponse`, `AwaitingFollowUpResponse`
- Per-state handlers for success, failure, and timeout scenarios
- State transition logging: `→ State: [StateName]`

**Context Wrapper Records**
- `Morgana.Records`: `ProcessingContext`, `GuardCheckContext`, `ClassificationContext`, `AgentContext`, `FollowUpContext`, `AgentResponseContext`, `LLMCheckContext`
- `Morgana.AI.Records`: `ClassificationContext` (ClassifierActor)
- Preserve sender context through async operations and state transitions

**Error Handling & Resilience**
- Per-state `Status.Failure` handlers with explicit fallback responses
- Fail-open GuardActor (assumes compliant on LLM errors)
- Fail-safe ClassifierActor (defaults to "other" intent on errors)
- Automatic state recovery on timeout

**Enhanced Observability**
- Intent routing with agent path visibility
- Context synchronization broadcast tracking
- Guard violation detection with specific term logging
- Classification confidence metrics
- Agent completion status tracking

**PipeTo Pattern Integration**
- Success/failure handlers for all async actor operations
- Automatic `Status.Failure` propagation on faults
- Non-blocking message processing across actor hierarchy

### 🔄 Changed
**Actor Pattern Migration**
- **BREAKING**: Replaced `Ask<T>` with `Become/PipeTo` pattern in all actors
- Eliminated temporary actors and lifecycle leaks
- Explicit sender preservation through context wrappers

**Architecture Improvements**
- ConversationSupervisorActor: Implicit → explicit state machine
- RouterActor: Blocking → non-blocking routing
- GuardActor: Synchronous → async LLM policy checks
- ClassifierActor: Fire-and-forget → structured fallback pipeline

**Infrastructure Consolidation**
- Centralized logging in `MorganaActor` (eliminated duplication across 6 actors)
- Unified timeout handling (60s default, overridable per actor)
- Context wrappers organized by project namespace (`Morgana.Records` vs `Morgana.AI.Records`)

### 🐛 Fixed
- Fixed actor lifecycle leaks from temporary `Ask<T>` actors
- Fixed sender context loss in async operations
- Fixed inconsistent error handling across actors
- Fixed timeout behavior inconsistencies

### 🚀 Future Enablement
This refactoring unlocks:
- Akka.Persistence for event sourcing
- Akka.Cluster for distributed deployment
- Akka.Streams for high-throughput pipelines
- Custom supervision hierarchies
- Production monitoring dashboards
- Per-state circuit breakers


## [0.3.0] - 2025-12-22

### ✨ Added
- Added `ILLMService` implementation for **Anthropic** -> Morgana is now able to talk with **Claude**
- Added support for configuring `ILLMService` implementation with setting `LLM:Provider` (_AzureOpenAI_, _Anthropic_)
- Introduced abstraction of **MorganaLLMService** to factorize `ILLMService` implementations
- Introduced abstraction of **MorganaTool** to factorize LLM tool translations into `AIFunction`
- Introduced **MorganaContextProvider** implementing `AIContextProvider` for stateful context management
- Introduced **AgentThread** for framework-native multi-turn dialogue management via **Microsoft.Agents.AI**
- Added `OnSharedContextUpdate` callback mechanism in `MorganaContextProvider` for P2P synchronization
- Added `MergeSharedContext()` method with first-write-wins strategy for conflict resolution
- Added declarative shared variable detection via `Shared: true` parameter attribute in prompts.json
- Added serialization/deserialization support in `MorganaContextProvider` for future persistence capabilities
- Added comprehensive logging of shared variable tracking and context sync operations

### 🔄 Changed
- **BREAKING**: Eliminated manual conversation history management from `MorganaAgent` (`List<(string role, string text)> history`)
- **BREAKING**: Tools now receive `Func<MorganaContextProvider>` lazy accessor instead of direct context access
- `ConversationSupervisorActor` has been refactored in order to act as a message-driven state machine
- Context variables are now managed through `Dictionary<string, object>` in `MorganaContextProvider` instead of manual tracking
- `RouterActor` has been enhanced to serve dual purpose: **intent routing + context synchronization message bus**
- Agent initialization flow improved with lazy `AgentThread` creation (`aiAgentThread ??= aiAgent.GetNewThread()`)
- MorganaAgent.ExecuteAgentAsync() simplified leveraging framework-native conversation history
- Tool implementations refactored to follow consistent lazy `context provider accessor` pattern
- Introduced `ActorSystemExtensions` to ease and centralize Akka.NET actor resolution

### 🐛 Fixed
- `ToolParameter` informations were not sent to AIFunction, so LLM was not truly aware of them
- Context variable state synchronization across multiple agents
- Memory leaks from manual history management


## [0.2.0] - 2025-12-17

### ✨ Added
- Decoupled **Morgana** (chatbot) from **Cauldron** (SignalR frontend)
- Introduced **Morgana.AI** project to decouple AI-related capabilities from **Morgana**
- Introduced **MorganaAgent** abstraction to specialize actors requiring an AIAgent-based LLM interaction
- Introduced **IPromptResolverService** to decouple prompt maintenance burden from Morgana actors
- Given `IPromptResolverService` a default implementation based on JSON configuration (**prompts.json**)
- Introduced **IAgentRegistryService** for automatic discovery of Morgana agents at application startup
- Given `IAgentRegistryService` a default implementation based on reflection done via **HandlesIntent** attribute
- Enforced bidirectional validation of classifier prompt's intents VS declarative Morgana.AI agents
- Introduced **ToolAdapter** to ease the creation of AIFunction directly from tool definitions 

### 🔄 Changed
- Unified `InformativeAgent` and `DispositiveAgent` under a new intent-driven **RouterActor**
- Removed userId information from the basic fields sent to every actor/agent 
- Send button has been properly styled as a "magic witch's cauldron" with glowing effects

### 🐛 Fixed
- Resolved corner cases of multi-message which could be sent to Morgana


## [0.1.0] - 2025-12-10

### ✨ Added
- Initial public release of **Morgana** and **Morgana.Web**
- Multi-turn conversational pipeline with supervised agent orchestration
- Integration with **Microsoft.Agents.AI** for LLM-based decision and tool execution
- Dedicated **ConversationManagerAgent** for per-session lifecycle handling
- Policy-aware **Guard Agent** ensuring compliance and professional tone
- Real-time conversational streaming through **SignalR**
- InternalExecuteResponse messaging to expose the concrete executor agent
- BillingExecutor enhanced with local memory and `#INT#` interactive protocol

### 🔄 Changed
- Supervisor routing stabilized for multi-turn flows
- Clarified agent responsibilities and improved modularity
- Updated intermediate agents to support transparent executor forwarding

### 🐛 Fixed
- Resolved context loss during multi-step billing interactions
- Eliminated routing loops caused by agents self-handling fallback messages
- Fixed inconsistent actor instantiation across conversations
