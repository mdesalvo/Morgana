# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).


## [0.24.0] - UNDER DEVELOPMENT
### 🎯 Major Feature: Grimoire — the Rich-TTY Reference Channel
This release introduces **Grimoire**, a third reference channel that completes the **channel × capability matrix**: where Cauldron is the rich browser client and Rune is the poor-but-honest CLI, Grimoire is the **rich terminal** — the quadrant that was empty until now.
Grimoire is the textual sibling of Cauldron — declaring the **full capability profile** (rich cards, quick replies, streaming, markdown, no length cap) over the **webhook** delivery mode. This makes Grimoire the proving ground for rendering Morgana's *vanilla* richness in a terminal — markdown becomes styled ANSI, rich cards become bordered boxes, quick replies become an arrow-key selector.
Architecturally it reaffirms that Morgana adapts on the **capability profile, not the transport**: Grimoire and Rune share the very same webhook delivery, yet land at opposite ends of the expressivity spectrum purely by what they declare at the handshake.

### ✨ Added
- **Grimoire** reference channel at `Channels/Grimoire/` — a Spectre.Console CLI (Kestrel-hosted console app, HTTPS 5004) on the **webhook** delivery mode with the **full capability profile** (`SupportsRichCards/QuickReplies/Streaming/Markdown = true`, no `MaxMessageLength`). Onboards as its own JWT issuer (`iss=grimoire`, dedicated `Issuers[]` entry and symmetric key) and ships its own `Grimoire.Dockerfile`, a `profiles: ["tui"]` docker-compose service (launched interactively, like Rune) and Docker Hub publishing step — proving the channel abstraction supports a *rich* TTY with zero changes to Morgana core
- **Terminal markdown rendering** (`MarkdownTerminalRenderService`): walks the Markdig AST into inline-styled, pre-wrapped single-row Spectre `Markup`s — headings, bold/italic, inline & fenced code, lists, links, blockquotes and horizontal rules — rendered **live during streaming** (re-parsed each typewriter tick, no raw→formatted snap on commit), at parity with Cauldron's bubble
- **Terminal rich-card mapper** (`RichCardTerminalRenderService`): Spectrizes a `RichCard` into a hand-drawn bordered box of single-row markups covering all eight component types (text_block, key_value, divider, list, section, grid, badge, image), chrome tinted by speaker colour and body in a neutral grey ramp, at parity with Cauldron's card component tree
- **Terminal quick-reply selection** (`QuickReplyTerminalRenderService`): the offered replies *become* the prompt for the turn — an arrow-key selector driven inside the live display (the keys move a `❯` caret, Enter sends the reply's `Value`, Esc exits), blocking free-text until answered, at parity with Cauldron locking its textarea
- **Per-turn streaming** with a Cauldron-style typewriter (`AnsiConsole.Live` + two-buffer paced reveal), resize-aware via the `IViewportResizeWatcher` pattern
- **Conversation scrollback** (pager): review long, verbose conversations that outgrow the viewport — ↑/←/PageUp toward older content, ↓/→/PageDown back toward the present (±5 rows), with ▲/▼ header glyphs lighting when scrolling that direction is available. Enabled only at rest (suppressed mid-stream and during a pending quick-reply), reset to live on every send

### 🔄 Changed
- **Rune gains conversation scrollback**, ported from Grimoire — the same pager (↑/←/PageUp · ↓/→/PageDown, ±5 rows, ▲/▼ header glyphs) so a long conversation that exceeds the viewport is no longer lost off the top. This is a **UX-only** addition: Rune's capability profile is **deliberately untouched** (all rich features off, `MaxMessageLength=500`), so it keeps its role as *the* reference channel that exercises `MorganaChannelAdapter`'s degradation path
- **Framework formatting policy** (`morgana.json`) no longer forbids markdown: agents are now free to emit light markdown (emphasis, inline code, lists, headings, rules) in their message text. Channels that can't render it are downgraded automatically downstream by the channel adapter, so the LLM expresses formatting freely and the right surface adapts — Cauldron and Grimoire render it, Rune strips it
- **Wire contracts consolidated into `Morgana.Contracts`** — the DTOs exchanged between Morgana and its channels (handshake metadata/capabilities/coordinates, outbound message envelope, quick replies, rich cards and their components hierarchy, start/send request bodies and webhook `StreamChunkRequest` chunk body) were previously duplicated in 4 places (Morgana.AI + each of Cauldron/Rune/Grimoire). They now live once in a new **zero-dependency `Morgana.Contracts`** package: Morgana.AI references it and the 3 reference channels were rewired from their hand-maintained `Messages/Contracts/` copies to a **direct project reference** — a single source of truth, no more lockstep edits
- **Deterministic container builds** — `Morgana.Examples` now references `Morgana.AI` by **project reference** instead of a version-pinned NuGet `PackageReference`, and the channel/server Dockerfiles stage the first-party project subtrees and mirror the repo layout under `/src`. The Docker build no longer has to resolve the in-development framework version from the public feed (which previously broke with `NU1102` until the version was published), making the **image build self-contained and reproducible**
- Updated `Microsoft.Agents.AI` dependency to 1.7.0

### 🐛 Fixed
- Rune scrollback row budgeting and text wrapping miscounted wide/zero-width glyphs

### 🚀 Future Enablement
- **Rich-TTY domain experiences** — Grimoire proves Morgana's *full* expressive surface (streaming, markdown, rich cards, quick replies) lives natively in a terminal, with no browser and no HTML. Pair it with your own plugins and your domain AI becomes a **rich command-line experience**: an ops console that renders structured cards, a runbook agent with guided quick replies, a headless-but-expressive CI assistant — everything Cauldron offers, shipped straight to the shell. Where 0.21's Rune opened *TTY-native domain AI*, Grimoire makes it **TTY-native and visually rich**
- **Slash-command agentic extensions** — Grimoire's in-`Live` input model already multiplexes typed text, an arrow-key quick-reply selector and a scrollback pager over the same keyboard. That client-side command substrate is the natural foundation for local `/commands` (clear, export, jump-to-turn, search, theme) — turning the rich terminal from a pure conversation surface into an **interactive agentic console** without round-tripping every keystroke to Morgana


## [0.23.0] - 2026-05-22
### 🎯 Major Feature: Magic Dust — Token-Budget Protection
This release introduces **Magic Dust**, a per-conversation **lifetime token budget** that guards Morgana **orthogonally to the rate limiter**: where the rate limiter caps message *frequency*, Magic Dust caps token *consumption*.
Every conversation is born with a finite budget of dust; each LLM call (guard, classifier, agent, presenter) burns some, and the running balance is **artistically represented as the quantity of magic dust left in the cauldron** — a fuel-gauge the user watches deplete.
The budget is **cache-aware** (cached prompt tokens cost a fraction, 1-hour cache writes cost more, mirroring real provider economics), persisted in the per-conversation SQLite database so it survives actor decommission and resume, and enforced **fail-open** so a storage fault never blocks the user.
As the budget drains Morgana emits **one-shot advisory warnings at 70% and 90%**; once it is spent the conversation is **terminal** — the lockout is surfaced proactively at end of turn (and re-surfaced on resume) and the only way forward is a brand-new conversation.

### ✨ Added
- **Magic Dust** per-conversation lifetime token-budget guard (`IDustLimitService` / `SQLiteDustLimitService`), orthogonal to `IRateLimitService` — consumption vs frequency — failing open on any storage error
- Per-provider **cache-aware dust pricing** (`MagicDustPricing`): fresh / cache-read / cache-creation input tokens are weighted independently so the budget tracks real LLM cost, not raw token count
- Dust state persisted in the per-conversation SQLite database (`dust_budget` + `dust_usage_log`, schema v5), with one-shot 70% / 90% advisory warnings and a terminal lockout when the budget is exhausted
- Cauldron **`DustMeter`** — a depleting magic-dust fuel-gauge in the header, rehydrated from the persisted budget on conversation resume
- Rune sticky-header dust gauge for the TTY channel
- `morgana.dust.consumed` OpenTelemetry counter (tagged by `llm_role` and conversation) for cost observability

### 🔄 Changed
- Updated `Microsoft.Agents.AI` dependency to 1.6.2

### 🐛 Fixed
- The Cauldron textarea and send button were live during the initial presentation-load window
- MCP tool registration aborted permanently when a serverless or horizontally-scaled MCP host dropped the session between connect and tool discovery (the MCP specs mandate HTTP 404 on a session-bearing request)

### 🚀 Future Enablement
- **Adaptive Dust Pricing & Budget Analytics** — With `dust_usage_log` capturing per-call, per-role token economics and the `morgana.dust.consumed` counter feeding OpenTelemetry, operators can build **per-conversation cost dashboards** and graduate from a static `BudgetPerConversation` to **adaptive budgets** tuned per tenant, channel or agent mix — turning the dust model into a data-driven cost-governance lever rather than a fixed ceiling.
- **Economic Backpressure & Graceful Degradation** — A live dust balance opens the door to **budget-aware orchestration**: as dust runs low Morgana could automatically shed expensive steps (skip the classifier on obvious follow-ups, shrink the history context window, prefer a cheaper provider) so a conversation degrades gracefully toward its budget instead of hitting a hard wall, maximizing usefulness per token spent.


## [0.22.0] - 2026-05-14
### 🎯 Major Feature: System Prompt Caching for Anthropic
This release unlocks **dramatic cost reduction** for Anthropic users by exploiting **Anthropic's native system prompt caching** with 1-hour TTL.
System prompts (containing Morgana/Agents personalities, global policies and formatting rules) are automatically marked for caching via `cache_control: ephemeral` and reused across every conversation turn. A single system prompt is cached once and hit repeatedly, reducing token costs by **60%+ on high-volume deployments**.
The release also fortifies Morgana against Claude 4.6+ **no-prefill constraints** that previously caused `AnthropicBadRequestException` on trailing assistant messages, ensuring compatibility with the latest Claude models while maintaining prompt caching semantics intact.
### 🎯 Major Feature: Conversation-Scoped Shared Context Registry
This release replaces **fragile in-memory P2P broadcast** with a **durable, first-write-wins shared context registry** persisted in the per-conversation SQLite database.
Shared variables (marked with `Shared: true` in agent tool configurations) now write directly to a dedicated `shared_context` table, eliminating the O(N) hydration cost and surviving **actor decommission cycles**.
Previously, when an agent set a shared variable and then was decommissioned before routing to other agents, that variable was lost to dead letters.
Now every agent loads the shared registry at the start of each turn and merges incoming shared variables with first-write-wins collision resolution, making the entire multi-agent system **resilient to Akka.NET actor lifecycle events** while maintaining **cross-agent context transparency**.

### ✨ Added
- Sensible LLM cost reduction for Morgana experiences based on Anthropic, thanks to system prompt caching
- Interesting LLM-agnostic cost reduction, thanks to Morgana's presentation message caching
- Stabilize Morgana's behavior with Claude 4.6+ models, which are intolerant against trailing assistant messages
- Replace cross-agent context broadcast with conversation-scoped `shared_context` registry

### 🔄 Changed
- Use `OpenTelemetryChatClient` to get automatic `gen_ai.*` telemetry across LLM providers
- Bump Rune's `MaxMessageLength` advertised budget capability to 500
- Restyled Morgana's messaging avatar in Cauldron
- Updated `Microsoft.Agents.AI` dependency to 1.6.1
- Updated `Microsoft.Extensions.AI` dependency to 10.6.0
- Updated `ModelContextProtocol.Core` dependency to 1.3.0

### 🐛 Fixed
- Make Rune exit cleanly when its host terminal is killed brutally, so the container terminates and docker releases `morgana-network` instead of leaving it attached and tripping `compose down` with "_Resource is still in use_"
- Make Rune's viewport resize-aware with flat-row anchor and platform-specific (SIGWINCH/poll) watcher
- Handle `Terminated` message in `ConversationManagerActor` to prevent `DeathPactException
- Solve memory leak due to unbounded growth of MCP executor cache
- Solve memory leak due to undisposed OTel spans in `ConversationSupervisorActor.PostStop`
- Filter intermediate tool-use assistant messages from rendered history (observed on Haiku 4.5)

### 🚀 Future Enablement
- **LLM Provider Cost Optimization via Cache Analytics** — With system prompt caching now active across Anthropic (and extensible to Azure OpenAI and other providers), Morgana can surface **cache performance dashboards** showing hit rate, token savings per provider, cost-per-conversation trends, and cache density metrics. Combined with OpenTelemetry's `gen_ai.usage.cache_*` attributes, operators gain data-driven visibility into which LLM providers and prompt strategies deliver best cost-performance, unlocking competitive cost optimization across multi-provider deployments.
- **Ephemeral Shared Context with Time-to-Live** — The conversation-scoped `shared_context` registry can evolve to support **per-variable TTL** (`expiresAt` timestamp), enabling agents to store temporary context (authorization tokens, session IDs, rate-limit state, derived computations) that auto-expire after configurable intervals. This unlocks session-like behavior where sensitive transient data never persists longer than needed, reducing storage footprint and improving security posture without explicit cleanup logic.


## [0.21.0] - 2026-04-25
### 🎯 Major Feature: Channel-Aware Adaptive Messaging
This release turns Morgana into a true **multi-channel** conversational AI framework with **channel-driven capability negotiation**.
The outbound path is no longer hard-wired to `SignalR+Cauldron`: channels now declare their **capability budget** (rich cards, quick replies, streaming, markdown, max message length), Morgana adapts its outbound expressivity to that budget automatically.
On the inbound side, Morgana no longer treats Cauldron as its only caller: the single-origin CORS whitelist is gone and JWT authentication moves to a **per-issuer trust model**, so each channel onboards with its own issuer name and signing key, cryptographically isolated from the others.
### 🎯 Major Feature: Rune — the CLI Reference Channel
This release introduces **Rune**, a second reference channel that brings Morgana **out of the browser and into the terminal** — a colorful, interactive CLI that runs wherever a shell does, from developer laptops to CI pipelines to SSH jump boxes. Same Morgana, same agents, same spells — a **new doorway** to embed her in automation workflows, pair her with operational tooling, or hand her to users who never leave the command line. Rune is the living proof that Morgana is truly channel-agnostic, opening the imagination to wherever she could live next — IVR, SMS, chat apps, embedded devices, the things you haven't thought of yet.

### ✨ Added
- Added **Markdown rendering** to Cauldron: assistant messages are now parsed through **Markdig** and rendered as styled HTML, with a dedicated `markdown.css` stylesheet that integrates with the existing dark theme and CSS variables
- Introduced `IChannelService` as the **outbound channel abstraction** with a generic `ChannelMessage` DTO, so producers (supervisor, manager, presenter, controllers) target a transport-agnostic contract instead of `IHubContext<MorganaHub>` directly; `SignalRChannelService` ships as the first full-capability implementation, and the Cauldron client-side contracts are grouped under a dedicated `Cauldron.Messages.Contracts` namespace
- Introduced `MorganaChannelAdapter` as the **producer-side degradation gate**: every outbound `ChannelMessage` is measured against the channel's advertised `ChannelCapabilities` and, when it exceeds the budget, rewritten by an LLM-guided pass that transcodes rich cards into prose, inlines quick replies, strips markdown and honours length limits — with a Markdig-based template fallback if the LLM call fails, so degradation is never silent
- Introduced `AdaptingChannelService` as a **transparent decorator** around every `IChannelService` implementation: wraps every `SendMessageAsync` through the adapter without touching any producer, making degradation the default behaviour on the whole outbound path
- Introduced **per-conversation `ChannelCapabilities` with a persistence-backed handshake**: clients announce their capability budget at `POST /conversation/start`; Morgana persists it in the new `channel_metadata` table (SQLite schema v3) and exposes it through the new `IChannelMetadataStore` registry, so `AdaptingChannelService` degrades per conversation and `ConversationSupervisorActor` stamps the budget on every agent turn
- Introduced **Rune**, a second reference channel sibling to Cauldron at `Channels/Rune/` — a Spectre.Console CLI (Kestrel-hosted console app, HTTPS 5003) that exercises the **webhook delivery mode** (`deliveryMode=webhook`) and the **"poor but honest" capability profile** (all rich features off, `MaxMessageLength=200`). Rune onboards as its own JWT issuer (`iss=rune`, separate `Issuers[]` entry with its own symmetric key) and ships its own `Rune.Dockerfile`, docker-compose service block and Docker Hub publishing step — proving end-to-end that the channel abstraction works with zero changes to Morgana core
- Relocated reference channels under a dedicated `Channels/` root and co-located each project's `Dockerfile` with its source (`Morgana/Morgana.Dockerfile`, `Channels/Cauldron/Cauldron.Dockerfile`, `Channels/Rune/Rune.Dockerfile`), so the repo layout mirrors the channel abstraction and each container recipe lives next to what it builds; Docker build context stays at the repo root so internal COPY paths remain stable

### 🔄 Changed
- **BREAKING**: The `Morgana:CauldronURL` setting has been removed from `appsettings.json`, `docker-compose.yml` and all references; the CORS policy (renamed from `AllowBlazor` to `Channel`) now uses `SetIsOriginAllowed(_ => true)` in place of a single-origin whitelist. JWT authentication is the sole trust boundary at the channel edge — any origin may reach Morgana, but only signed tokens from declared issuers are accepted
- **BREAKING**: The `Morgana:Authentication:Enabled` toggle has been removed. Authentication is now unconditionally enforced: every request must carry a valid bearer token, there is no config flag to bypass it. Migration: delete the `Enabled` key from your `Morgana:Authentication` section; any deployment that relied on `Enabled: false` for development must now mint valid JWTs (or swap `IAuthenticationService` for a dev-only stub in DI)
- **BREAKING**: JWT authentication now uses **per-issuer signing keys**. `Morgana:Authentication:SymmetricKey` and `Morgana:Authentication:ValidIssuers[]` have been replaced by `Morgana:Authentication:Issuers[]`, an array of `{ Name, SymmetricKey }` entries. `JWTAuthenticationService` peeks the `iss` claim, looks up the matching entry and validates the signature with that issuer's own key; unknown issuers are rejected outright. Migration: replace the flat `{ SymmetricKey, ValidIssuers: ["cauldron"] }` pair with `{ Issuers: [{ "Name": "cauldron", "SymmetricKey": "..." }] }`
- Updated `Microsoft.Agents.AI` dependency to 1.2.0
- Updated `Microsoft.Extensions.AI` dependency to 10.5.0
- Updated `ModelContextProtocol.Core` dependency to 1.2.0
- Updated `OllamaSharp` dependency to v5.4.25

### 🐛 Fixed
- Race condition on conversation resume where `MorganaController` created the `ConversationSupervisorActor` directly in parallel to `ConversationManagerActor.HandleCreateConversationAsync`, causing an `InvalidActorNameException` ("Actor name supervisor-... is not unique"), killing the manager and leaving the conversation without registered channel metadata
- Hard-coded `"Morgana"` sentinel was sent as `RestoreActiveAgent.AgentIntent` when no persisted active agent existed; the controller now simply skips the restore message entirely in that case, and the supervisor's corresponding sentinel branch has been removed
- Ephemeral UI variables (`rich_card`, `quick_replies`) could leak across turns when the LLM populated them via `SetRichCard`/`SetQuickReplies` and the streaming phase or the persistence save threw before reaching the happy-path `DropVariable` calls
- Reinforced `SetRichCard` prompting with mandatory JSON well-formedness rules both in the `RichCardUsage` global policy and in the tool parameter description, to reduce the rare cases where LLMs emit malformed JSON (missing braces, trailing commas) and the card is silently dropped
- Added a new `SessionContinuation` critical global policy telling the agent to treat any current user message as a fresh, independent request when its history contains previous closure exchanges, instead of mirroring past farewells and producing another closure
- Cauldron's textarea and send button could not disable when live SignalR messages carried active quick replies (e.g. the closure message with "I still need you" / "We're done, thanks"), letting the user bypass the quick reply gate with free-text input
- `RouterActor.HandleAgentStreamChunk` fallback branch for orphan stream chunks used `Context.Parent.Tell(chunk)`, which resolved to the `/user` guardian (the router is created flat under the guardian, not as a child of the supervisor) and silently dropped the chunk to dead letters
- Cauldron circuit crashed with `InvalidOperationException: Collection was modified; enumeration operation may not execute` when SignalR `ReceiveMessage` / `ReceiveStreamChunk` callbacks mutated `ChatStateService.ChatMessages` on the SignalR dispatch thread while Blazor's `BuildRenderTree` enumerated the same list on the circuit thread
- History resume could reconcile quick replies from the previous assistant turn onto a later rich card, while dropping the intermediate assistant text entirely

### 🚀 Future Enablement
- **Custom channels (IVR, SMS, RCS, Twilio, WhatsApp, plain HTTP client, …)** — With the `IChannelService` abstraction, the `AdaptingChannelService` decorator and the persistence-backed capability handshake all in place, a new outbound channel can plug in declaring its own capability budget at conversation start and get automatic degradation of any rich message without any change to producers, actors or prompts
- **TTY-native domain AI** — Pair Rune with your own Morgana plugins (your intents, your agents, your prompts, your tools) and your **domain AI becomes a first-class command-line experience**: no frontend work, no browser, no HTML. A developer-tooling companion, an ops runbook agent, a headless CI assistant, a business-domain expert in the terminal — whatever your `agents.json` describes, now shipped straight to the shell


## [0.20.1] - 2026-04-08
### 🐛 Fixed
- Dead-letter loop where content filter rejections caused the supervisor to reply to itself instead of the user, leaving the turn unanswered


## [0.20.0] - 2026-04-08
### 🎯 Major Feature: JWT Authentication between Cauldron and Morgana
This release introduces **JWT bearer token authentication** to secure all communications between Cauldron (frontend) and Morgana (backend).
Cauldron self-issues signed JWT tokens injected automatically into every HTTP request and SignalR connection; Morgana validates them at the API boundary before processing.
The shared symmetric key is configured via environment variable (`JWT_SYMMETRIC_KEY`) or User Secrets, and the feature can be toggled off for development via `Morgana:Authentication:Enabled`.
### 🎯 Major Feature: Cauldron Extension Points
This release completes the **Cauldron extension points** model: `IChatStateService`, `IConversationLifecycleService`, `IStreamingService` and `ILandingMessageService` join the existing suite of pluggable interfaces (`IConversationStorageService`, `IConversationHistoryService`), making every behavioural concern of Cauldron independently overridable via DI without touching a single line of code.

### ✨ Added
- Introduced `IChatStateService` as an **extension point for chat UI state management**: `ChatStateService` ships as the default implementation (message list, temporary messages, agent tracking, sending state, UI queries) and can be replaced in DI with any alternative strategy without touching the component layer
- Introduced `IConversationLifecycleService` as an **extension point for conversation lifecycle operations**: `ConversationLifecycleService` ships as the default implementation (REST-based start/resume/clear, history loading with agent-boundary hints, fallback-to-new-conversation) and can be replaced in DI with any alternative backend without touching the component layer
- Introduced `IStreamingService` as an **extension point for streaming state management**: `StreamingService` ships as the default implementation (chunk buffering, configurable typewriter timer, auto-cleanup on buffer drain) and can be replaced in DI with any alternative rendering strategy without touching the component layer
- Introduced `ILandingMessageService` as an **extension point for landing message selection**: `LandingMessageService` ships as the default implementation (random selection from configuration-driven message pool) and can be replaced in DI with any alternative strategy (static templates, tenant-specific content, CMS-driven messages, A/B variants) without touching the component layer
- Improved **accessibility (a11y)**: added `aria-live` regions for real-time message announcements (polite for chat messages, assertive for error banners); `role="status"` on connection indicator and typing indicator; `aria-label` on send button, new conversation button, message input, message rows and typing indicator; `aria-hidden="true"` on decorative SVGs and emoji icons
- It is now available the **Morgana Handbook** as a quick technical intro to Morgana's conversational AI framework

### 🔄 Changed
- `Index.razor` reduced from ~1270 lines to ~500 lines, now acts as a thin UI orchestrator that delegates logic to backend services
- Renamed configuration path `Morgana:Cauldron:BaseUrl` to `Morgana:CauldronURL`
- Cauldron settings moved under `Cauldron` root key for clearer semantic (was `Morgana`)
- Improved OpenTelemetry observability: accurate turn span, metrics and exception recording
- Improved health check endpoint with actor system liveness detection
- Updated `Microsoft.Agents.AI` dependency to 1.0.0
- Updated `ModelContextProtocol.Core` dependency to 1.1.0

### 🐛 Fixed
- `Index.razor@isSending` not being reset on HTTP error response in `SendMessageAsync`, which permanently blocked user input after a failed send

### 🚀 Future Enablement
- **Secure multi-tenant deployment** — With JWT authentication in place, Morgana is ready for scenarios where multiple Cauldron instances (or third-party frontends) connect to a shared Morgana backend, each identified by their token claims.
- **Audit and conversation ownership** — `UserId` propagation into the actor system lays the groundwork for per-user conversation history, access control and compliance audit trails.
- **Custom frontend experiences** — With all Cauldron services behind interfaces (`IChatStateService`, `IConversationLifecycleService`, `IStreamingService`, `ILandingMessageService`), alternative implementations can be swapped via DI to customize chat behavior, streaming rendering, or conversation management without modifying any Razor component.


## [0.19.0] - 2026-03-20
### 🎯 Major Feature: Ollama and OpenAI support
This release adds support for **Ollama** and **OpenAI** providers, alongside the existing **Anthropic** and **Azure OpenAI** implementations.
### 🎯 Major Feature: Morgana.AI Extension Points
This release completes the **Morgana.AI extension points** model: `IGuardRailService`, `IClassifierService` and `IPresenterService` join the existing suite of pluggable interfaces (`ILLMService`, `IConversationPersistenceService`, `IRateLimitService`, `IAgentConfigurationService`, `IPromptResolverService`, `ISignalRBridgeService`), making every behavioural concern of the actor system independently overridable via DI without touching a single line of code.

### ✨ Added
- Added `Ollama` as `MorganaLLM` implementation, enabling Morgana to connect to local Ollama models (e.g: gpt-oss:20b) using native `OllamaApiClient` wrapped via `OllamaSharp` package.
- Added `OpenAI` as `MorganaLLM` implementation, enabling Morgana to connect to OpenAI services using native `OpenAIClient` wrapped via Microsoft.Extensions.AI abstraction.
- Introduced `IGuardRailService` as an **extension point for content moderation**: `LLMGuardRailService` ships as the default implementation (two-level profanity + LLM policy check) and can be replaced in DI with any alternative backend (e.g. Microsoft Purview, Azure AI Content Safety, ...) without touching the actor system.
- Introduced `IClassifierService` as an **extension point for intent classification**: `LLMClassifierService` ships as the default implementation (agents.json + morgana.json LLM classification) and can be replaced in DI with any alternative backend (e.g. Azure AI Language, Amazon Lex, Google Natural Language AI, ...) without touching the actor system.
- Introduced `IPresenterService` as an **extension point for welcome presentation**: `LLMPresenterService` ships as the default implementation (LLM-driven welcome message with intent-based quick replies) and can be replaced in DI with any alternative strategy (static templates, tenant-specific content, CMS-driven messages, A/B variants) without touching the actor system.

### 🔄 Changed
- `GuardActor`, `ClassifierActor` and `ConversationSupervisorActor` have been refactored to delegate behavioural logic entirely to their respective extension point services — actors are now thin orchestration shells with no embedded LLM or business logic.
- Added setting `Morgana:ActorSystem:EnableGuardrail` as general switch for guardrail aspects (e.g: for Ollama development scenarios)
- Added setting `Morgana:ActorSystem:TimeoutSeconds` to tweak default actors/agents timeout (e.g: for Ollama development scenarios)
- Updated `Azure.AI.OpenAI` dependency to 2.9.0-beta.1
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-rc.4

### 🐛 Fixed

### 🚀 Future Enablement
- **Morgana.AI Extensibility** — Morgana.AI becomes a fully customizable conversational AI framework where the default implementations ship as sensible out-of-the-box baselines.
- **Local AI development** — With `Ollama` support, Morgana agents can run entirely on local infrastructure during development, eliminating cloud API costs and enabling air-gapped scenarios. Please note that Morgana's unique expressivity (quick replies, rich cards) requires a local AI model capable at least of **tool and function calling**, alongside a properly sized **context window** (e.g: `phi4-mini`, `gpt-oss:20b`).


## [0.18.0] - 2026-03-11
### 🎯 Major Feature: OpenTelemetry Distributed Tracing
This release introduces **end-to-end distributed tracing** across the entire Morgana conversation pipeline, providing deep observability for both technical diagnostics and functional conversation analytics. Traces are structured to be meaningful to **IT operators** (latencies, errors, TTFT) and **non-technical stakeholders** (intent, agent name, response preview).
### 🎯 Major Feature: Morgana.AI as NuGet
This release sets the milestone of distributing **Morgana.AI** as **NuGet** package, making it straightforward to model your specialized agents and package them as plugins ready to be discovered and executed by Morgana.

### ✨ Added
**OpenTelemetry Tracing Architecture**
- New `Morgana.AI/Telemetry/` module with `MorganaTelemetry` (ActivitySource + attribute constants) and `OpenTelemetryExtensions` (`AddMorganaOpenTelemetry()` registration)
- Per-turn span (`morgana.turn`) opened at the HTTP boundary in `ConversationController`, propagated into the Akka.NET actor system via `UserMessage.TurnContext`
- Child spans for each pipeline stage: `morgana.guard`, `morgana.classifier`, `morgana.router`, `morgana.agent`
- Explicit `ActivityContext` propagation through `UserMessage`, `ProcessingContext` and `AgentRequest` records — solving Akka.NET's ambient `Activity.Current` breakage across thread pool boundaries

**Span Attributes**
- `conversation.id`, `turn.user_message` (truncated 200 chars) for conversation correlation
- `guard.compliant`, `guard.violation` for content moderation visibility
- `classification.intent`, `classification.confidence` for routing diagnostics
- `router.intent`, `router.agent_path` for agent selection tracking
- `agent.name`, `agent.intent`, `agent.ttft_ms` (time-to-first-token), `agent.response_preview` (first 150 chars), `agent.is_completed`, `agent.has_quick_replies`
- `first_chunk` ActivityEvent on first LLM streaming chunk for precise TTFT measurement

**Flexible Exporter Configuration**
- Array-based exporter configuration supporting multiple simultaneous exporters
- Built-in exporters: `console` (development), `otlp` (Jaeger, Grafana Tempo, Azure Monitor, Datadog, ...)

```json
{
  "Morgana": {
    "OpenTelemetry": {
      "Enabled": true,
      "ServiceName": "Morgana",
      "Exporters": [
        { "Name": "console", "Enabled": true },
        { "Name": "otlp", "Enabled": true, "Endpoint": "http://localhost:4317" }
      ]
    }
  }
}
```

### 🔄 Changed
- Morgana's avatar is now animated (with magical glowing effects when thinking)
- Send button has been componentized and totally restyled
- Make streaming response mode configurable under `StreamingResponse:Enabled` appsetting
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-rc.3
- Updated `Microsoft.Extensions.AI` dependency to 12.4.0
- Updated `ModelContextProtocol.Core` dependency to 1.0.0

### 🐛 Fixed
- Docker images did not copy `Morgana.Examples.dll` into `plugins` directory, generating an agentless Morgana
- Ensure to queue shared context updates received before first agent session is established
- Fixed conversation resume not displaying assistant messages when using `Anthropic` as the LLM provider.

### 🚀 Future Enablement
- **Production observability** — With an OTLP backend (Jaeger, Grafana Tempo, Azure Monitor, ...), every Morgana conversation becomes fully navigable: intent distribution, per-agent TTFT trends, guard violation rates and pipeline latencies all visible on a single dashboard
- **Morgana Agents Ecosystem** — Being **Morgana.AI** a development foundation packed as NuGet, it lays the groundwork for an ecosystem of reusable, shareable agent libraries — paving the way for curated Morgana Agent Ecosystem where domain-specific plugins can be discovered, distributed and adopted across projects.


## [0.17.0] - 2026-02-11

### ✨ Added

### 🔄 Changed
- `PluginLoaderService` now follows *directories-to-scan* paradigm instead of *assemblies-to-scan*. Morgana gains a **true plugin system**!

### 🐛 Fixed
- Arguments of `SummarizingChatReducer` in `SummarizingChatReducerService.CreateReducer` were swapped (correct order: targetCount, threshold)
- Conversation history is now fully preserved after refresh: reducer applies only to LLM view, never to the storage

### 🚀 Future Enablement
- **Extensibility via plugin system**: Foundation for industrial evolution of the plugin system (e.g: even MorganaLLM implementations may be plugged)


## [0.16.0] - 2026-02-07
### 🎯 Major Feature: Rich Card System for Structured Data Visualization
This release introduces **Rich Cards**, a compositional visual presentation system that complements Quick Replies by transforming unstructured tool outputs into **scannable, elegantly organized visual cards**. LLMs can now construct sophisticated data presentations using **8 semantic building blocks** (text_block, key_value, divider, list, section, grid, badge, image) instead of overwhelming users with walls of text.

### ✨ Added
**SetRichCard System Tool**
- New LLM-callable system tool `SetRichCard` for creating structured visual cards from tool outputs
- **Compositional design**: 8 basic component types combine like LEGO blocks to create complex layouts
- **Component dictionary**: text_block (narrative), key_value (labeled data), divider (separation), list (enumeration), section (nestable grouping), grid (multi-column), badge (status indicators), image (multimedia)
- Supports up to **3 levels of nesting** for hierarchical information organization
- Maximum **50 components** per card with comprehensive validation

**Rich Card Architecture**
- **Backend**: Complete type hierarchy with `JsonPolymorphic` serialization for 8 component types
- **Data flow**: Agent → `GetRichCardFromContext()` → ephemeral extraction → propagation through actor pipeline → SignalR → Cauldron
- **Frontend**: 8 Razor components (RichCard.razor dispatcher + 8 type-specific renderers)
- **Morgana theme**: Purple/indigo gradients, glowing effects, depth-based visual hierarchy, mobile-responsive layouts

**Conversation Persistence for Rich Cards**
- Rich cards automatically saved and restored during conversation resume
- Follows exact pattern of Quick Replies: extracted from `SetRichCard` function calls, attached to subsequent assistant messages

### 🔄 Changed

### 🐛 Fixed

### 🚀 Future Enablement
- **Tool output standardization** - Rich Cards establish a pattern for tool developers: return structured JSON, let LLM handle presentation through SetRichCard, separating data retrieval from visualization concerns
- **Advanced visualizations** - Foundation for future component types (charts, timelines, progress indicators, interactive forms) while maintaining compositional architecture
- **Cross-agent consistency** - All agents now present complex data using same visual language, creating cohesive UX across domain boundaries (billing, contracts, support tickets, analytics)


## [0.15.0] - 2026-02-06
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
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-preview.260205.1
- Updated `ModelContextProtocol.Core` dependency to 0.8.0-preview.1

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
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-preview.260128.1
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
- Updated `Microsoft.Agents.AI` dependency to 1.0.0-preview.260121.1
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
- BillingExecutor enhanced with local memory and `#INT#` interactive protocol

### 🔄 Changed

### 🐛 Fixed
