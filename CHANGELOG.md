# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - UNDER DEVELOPMENT
### Added
- Introduced **IPromptResolverService** to decouple prompt handling and maintenance from actors/agents
- Give IPromptResolverService a default implementation based on JSON configuration (prompts.json)
- Decoupled **Morgana** (chatbot engine) from **Cauldron** (SignalR frontend for user interaction)
- Introduced **Morgana.AI** project to decouple AI-related capabilities from Morgana

### Changed
- Unified InformativeAgent and DispositiveAgent under a new **RouterAgent**
- Removed userId information from the basic fields sent to every actor/agent 
- Send button has been properly styled as a "magic witch's cauldron" with glowing effects
- Update NuGet dependencies (Microsoft.Extensions.AI, Microsoft.Agents.AI, Azure.AI.OpenAI)

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
