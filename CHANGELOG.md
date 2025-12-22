# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.4.0] - UNDER DEVELOPMENT

### ✨ Added
- **Layered Personality System**: Introduced two-tier personality architecture with global (Morgana) and agent-specific personalities
  - Global personality applied consistently across all agent interactions
  - Agent-specific personalities complement global traits for domain-appropriate behavior
  - Personality composition respects subordination principle (agents never contradict global character)
- **Global Policies Framework**: Centralized policy management system for system-wide behavioral rules
  - `GlobalPolicy` record type with `Name`, `Description`, and `Type` (Critical/Operational)
  - Policies automatically injected into all agent instructions
- **Tool Parameter Guidance Policies**: Declarative guidance for tool parameter handling
  - `ToolParameterContextGuidance`: Rules for context-scoped parameters
  - `ToolParameterRequestGuidance`: Rules for request-scoped parameters
  - Guidance automatically applied based on parameter `Scope` attribute

### 🔄 Changed

### 🐛 Fixed

## [0.3.0] - 2024-12-22

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
