# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [0.9.0] - UNDER DEVELOPMENT

### 🐛 Fixed
- FSM behavior of supervisor caused unregistration of timeout handler, leading to dead-letters at timeout
- Typing bubble color was always tied to color scheme of "Morgana" agent

### 🚀 Future Enablement
This release unlocks:
- 


## [0.8.1] - 2026-01-11

### 🐛 Fixed
- Morgana tools (GetContextVariable, SetContextVariable, SetQuickReplies) were not injected into MCP-only agents

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
