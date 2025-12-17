# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - UNDER DEVELOPMENT
### Added
- Introduced **MorganaAgent** abstraction to characterize actors requiring an AIAgent-based interaction with ILLMService
- Introduced **IPromptResolverService** to decouple prompt maintenance burden from Morgana actors/agents
- Given IPromptResolverService a default implementation based on JSON configuration (**prompts.json**)
- Introduced **IAgentRegistryService** for automatic startup discovery of Morgana agents
- Given IAgentRegistryService a default implementation based on reflection done on new **HandlesIntent** attribute
- Enforced bidirectional validation of classifier prompt's intents VS discovered Morgana agents
- Introduced **ToolAdapter** to ease the creation of AIFunction directly from tool definitions 
- Decoupled **Morgana** (chatbot engine) from **Cauldron** (SignalR frontend for user interaction)
- Introduced **Morgana.AI** project to decouple AI-related capabilities from Morgana

### Changed
- Unified InformativeAgent and DispositiveAgent under a new intent-driven **RouterAgent**
- Removed userId information from the basic fields sent to every actor/agent 
- Send button has been properly styled as a "magic witch's cauldron" with glowing effects

### Fixed
- Resolved corner cases of multi-message which could be sent to Morgana

## [0.1.0] - 2025-12-10
### Added
- Initial public release of **Morgana** and **Morgana.Web**.
- Multi-turn conversational pipeline with supervised agent orchestration.
- Integration with **Microsoft.Agents.AI** for LLM-based decision and tool execution.
- Dedicated **ConversationManagerAgent** for per-session lifecycle handling.
- Policy-aware **Guard Agent** ensuring compliance and professional tone.
- Real-time conversational streaming through **SignalR**.
- InternalExecuteResponse messaging to expose the concrete executor agent.
- BillingExecutor enhanced with local memory and `#INT#` interactive protocol.

### Changed
- Supervisor routing stabilized for multi-turn flows.
- Clarified agent responsibilities and improved modularity.
- Updated intermediate agents to support transparent executor forwarding.

### Fixed
- Resolved context loss during multi-step billing interactions.
- Eliminated routing loops caused by agents self-handling fallback messages.
- Fixed inconsistent actor instantiation across conversations.
