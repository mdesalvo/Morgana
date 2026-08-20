# Cauldron - Morgana's Reference Frontend

## What is Cauldron

Cauldron is a **Blazor Server** web application that serves as the reference channel for Morgana. It provides a rich chat UI with real-time streaming, quick reply buttons, rich cards, typing indicators, and conversation persistence across browser sessions. Communicates with the Morgana backend via REST API (conversation lifecycle, message sending) and SignalR (real-time message delivery, streaming chunks).

Cauldron lives at `Channels/Cauldron/` in the repo root, alongside other reference channels, separate from the `Morgana/` working directory.

## Project Structure

```
Channels/Cauldron/
  Program.cs                          # DI wiring and app pipeline
  Cauldron.csproj                     # .NET 10 Web SDK
  Cauldron.slnx                       # Solution (sibling to Rune.slnx)
  Directory.Build.props               # Shared build/version metadata
  Directory.Build.targets             # MSBuild target that regenerates root .env.versions on each build
  appsettings.json                    # Morgana URL, auth, streaming and landing-message config
  Properties/launchSettings.json      # Dev profile: https://localhost:5002
  Cauldron.Dockerfile                 # Multi-stage container build (root context)
  App.razor                           # Blazor root component
  _Imports.razor                      # Shared @using directives for components
  Pages/_Host.cshtml                  # Blazor Server host page
  Pages/Index.razor                   # Main chat page (single-page app)
  Components/                         # Reusable Blazor components
    RichCard.razor                    # Rich card container (dispatches to sub-renderers)
    RichCardTextBlock.razor           # text_block component
    RichCardKeyValue.razor            # key_value component
    RichCardDivider.razor             # divider component
    RichCardList.razor                # list component
    RichCardSection.razor             # section component
    RichCardGrid.razor                # grid component
    RichCardBadge.razor               # badge component
    RichCardImage.razor               # image component
    RichCardComponent.razor           # Polymorphic dispatcher (type discriminator → sub-renderer)
    QuickReplyButton.razor            # Quick reply button row
    FadingMessage.razor               # Temporary system warning banners (auto-dismiss)
    SendButton.razor                  # Animated send button with agent-specific styling
    ConfirmModal.razor                # Modal dialog for "New Conversation" confirmation
  Handlers/
    MorganaAuthHandler.cs             # DelegatingHandler: self-issues JWT tokens for Morgana API auth
  Interfaces/
    IChatStateService.cs              # Chat UI state management contract
    IConversationLifecycleService.cs  # Start/resume/clear conversation contract
    IConversationStorageService.cs    # Browser localStorage persistence contract
    IConversationHistoryService.cs    # History fetch contract
    ILandingMessageService.cs         # Welcome message contract
    IStreamingService.cs              # Streaming lifecycle contract
  Services/
    SignalRService.cs                 # SignalR client: connection, events, auto-reconnect
    ConversationLifecycleService.cs   # Orchestrates REST API + SignalR + storage
    ChatStateService.cs               # In-memory UI state (messages, agent, sending, etc.)
    StreamingService.cs               # Chunk buffering, typewriter timer, finalization
    ProtectedLocalStorageService.cs   # IConversationStorageService → ProtectedLocalStorage
    ConversationHistoryService.cs     # GET api/morgana/conversation/{id}/history
    LandingMessageService.cs          # Random welcome message during sparkle loader
    MarkdownRendererService.cs        # Markdig-based HTML rendering for message text
  Messages/
    ChatMessage.cs                    # UI-side message model (text, role, agent, quickReplies, richCard, streaming state)
    CauldronChannelMetadata.cs        # Channel identity + capability profile announced at handshake
  Program.cs                          # DI wiring and app pipeline
  Shared/MainLayout.razor             # Layout wrapper
  wwwroot/css/                        # Component-specific CSS (site.css, rich-card.css, quick-reply.css, etc.)
  wwwroot/images/                     # Morgana avatar images
  Widget/                             # Embeddable launcher (static assets only, published under /widget/)
    Widget.csproj                     # Razor Class Library, StaticWebAssetBasePath=widget, compiles no code
    wwwroot/morgana-widget.js         # Loader: origin discovery, shadow root, launcher, lazy iframe
    wwwroot/morgana-widget.css        # Launcher + panel styling, in Cauldron's palette
    wwwroot/morgana-animated.webp     # Morgana's face on the button
    wwwroot/morgana.html              # Mock third-party host page carrying only the script tag
    wwwroot/demo/*.webp               # Photography for that sample page only
```

## Architecture

### Communication with Morgana

```
Cauldron ──REST──→ Morgana.Web (MorganaController)
         ←SignalR── Morgana.Web (MorganaHub)
```

- **REST** (via `HttpClient` named "Morgana"): conversation start, resume, send message, get history
- **SignalR** (via `SignalRService`): receive messages (`ReceiveMessage`), receive streaming chunks (`ReceiveStreamChunk`), join/leave conversation groups

### DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `MorganaAuthHandler` | Transient | JWT token generation for HTTP and SignalR auth |
| `HttpClient` "Morgana" | Named + Scoped | REST API calls with auto Bearer token injection |
| `SignalRService` | Scoped | SignalR client lifecycle |
| `ILandingMessageService` | Singleton | Random welcome messages |
| `IConversationStorageService` | Scoped | `ProtectedLocalStorageService` — AES-encrypted localStorage |
| `IConversationHistoryService` | Scoped | `ConversationHistoryService` |
| `IChatStateService` | Scoped | `ChatStateService` |
| `IConversationLifecycleService` | Scoped | `ConversationLifecycleService` |
| `IStreamingService` | Scoped | `StreamingService` |

### Message Flow

**New conversation:**
1. `Index.razor.OnInitializedAsync` → check `ProtectedLocalStorage` for saved ID
2. No saved ID → `ConversationLifecycleService.StartConversationAsync()`
3. POST `api/morgana/conversation/start` with `ChannelMetadata.Cauldron`
4. Join SignalR group → await presentation message via `ReceiveMessage`

**Resume conversation:**
1. Saved ID found → `ConversationLifecycleService.ResumeConversationAsync(id)`
2. POST `api/morgana/conversation/{id}/resume` → 200 (with activeAgent) or 404
3. On 404 → fallback to `StartConversationAsync()`
4. On success → join SignalR group → GET history → populate chat messages
5. History load injects agent-turn-boundary completion messages

**Send message:**
1. User types + Enter (or click send) → `SendMessageAsync()`
2. Add user message + typing indicator to UI
3. POST `api/morgana/conversation/{id}/message`
4. Response arrives via SignalR:
   - **Streaming path**: `ReceiveStreamChunk` events → `StreamingService.HandleChunkAsync` → typewriter buffer → `ReceiveMessage` finalizes with server-authoritative text
   - **Non-streaming path**: `ReceiveMessage` → remove typing indicator → add message to chat

### Streaming (StreamingService)

- First chunk: removes typing indicator, creates streaming `ChatMessage`, starts typewriter `Timer`
- Typewriter tick: consumes N chars from buffer at configurable interval (default 15ms, 1 char)
- Finalization: `FinalizeStreaming(completeMessage)` overwrites text with server-authoritative version (may differ from streamed chunks if channel adapter rewrote the message), attaches quick replies + rich card
- Timer auto-stops when buffer empty and `IsStreaming == false`

### Chat State (ChatStateService)

Scoped service holding all UI state for one Blazor circuit:
- `ChatMessages` — full message list
- `TemporaryMessages` — ephemeral banners (rate limit warnings, errors) with auto-dismiss via `FadingMessage`
- `ConversationId`, `CurrentAgentName` — conversation identity
- `IsConnected`, `IsSending`, `IsInitialized`, `HasCheckedStorage` — UI state flags
- Agent display: base `"Morgana"` vs specialized `"Morgana (Billing)"` with different CSS colors
- `HasActiveQuickReplies()` / `HasTypingIndicator()` — input gating (disable textarea while quick replies are active or agent is typing)

## Authentication

`MorganaAuthHandler` is a `DelegatingHandler` that generates short-lived JWT tokens:
- **Algorithm**: HMAC-SHA256 with shared symmetric key from `Cauldron:Authentication:SymmetricKey`
- **Issuer**: `cauldron` — must be present in Morgana's `Morgana:Authentication:Issuers[]` list with a matching `SymmetricKey`; unknown issuers are rejected at the Morgana gate
- **Subject**: `cauldron-app`
- **Audience**: `morgana.ai`
- **Lifetime**: 5 minutes (re-generated per request)

Used by both the named `HttpClient` (automatic via handler pipeline) and `SignalRService` (via `AccessTokenProvider` callback in hub connection builder).

**Onboarding checklist for a fresh Morgana instance:**
1. Add an entry to `Morgana:Authentication:Issuers[]` in the destination Morgana configuration: `{ "Name": "cauldron", "SymmetricKey": "<at least 256 bit, base64>" }`
2. Put the same `SymmetricKey` under `Cauldron:Authentication:SymmetricKey` via user-secrets or env var (never commit)
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Cauldron/` (`:5002`)

## Wire Contracts (shared project)

The wire DTOs (`ChannelMessage`, `ChannelMetadata`, `ChannelCoordinates`, `ChannelCapabilities`, `QuickReply`, `RichCard`/`CardComponent`, …) are **no longer duplicated**: Cauldron takes a direct `ProjectReference` to **`Morgana.Contracts`** (`..\..\Morgana\Morgana.Contracts\Morgana.Contracts.csproj`) — the single source of truth shared with Morgana.AI — and consumes them under the `Morgana.Contracts` namespace. There is nothing to keep in lockstep anymore; change a contract once, in `Morgana.Contracts`.

The contract types are immutable records (init-only / positional), so code that used to mutate them in place (e.g. rich-card Markdown sanitization in `MarkdownRendererService`) now rebuilds via `with` expressions. Channel identity lives channel-side in `Messages/CauldronChannelMetadata.cs` (`CauldronChannelMetadata.Profile`), not on the shared contract.

The Docker build mirrors the repo layout under `/src` and stages the `Morgana.Contracts` subtree so the `ProjectReference` resolves (see `Cauldron.Dockerfile`).

Requests **and responses** both come from `Morgana.Contracts`: Cauldron posts `StartConversationRequest`/`SendMessageRequest` and reads back `StartConversationResponse`, `ResumeConversationResponse` and `ConversationHistoryResponse` — the very types `MorganaController` returns. No hand-written response mirrors survive.

`MorganaChatMessage` (the history element, with its `ChatMessageType` enum) is a wire DTO and lives in `Morgana.Contracts`, not in `Morgana.AI.Records`. It is **not** the UI model: `ConversationLifecycleService.MapToChatMessage` projects it onto `Messages/ChatMessage.cs`, which adds UI-only state the server knows nothing about (typing indicator, streaming flag, selected quick reply) and has its own richer `MessageType` (`Presentation`, `Error`). Keep the two enums apart — the mapping switch is exhaustive on purpose, so a value added server-side breaks the build here instead of silently landing on the wrong styling.

Channel-only shapes that are **not** part of `Morgana.Contracts` stay under `Messages/` (`ChatMessage`, `CauldronChannelMetadata`).

## Channel Handshake

At conversation start, Cauldron announces itself via the `CauldronChannelMetadata.Profile` singleton (`Messages/CauldronChannelMetadata.cs`):
```csharp
Coordinates = { ChannelName = "cauldron", DeliveryMode = "signalr" }
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true,
                 SupportsStreaming: true, SupportsMarkdown: true,
                 MaxMessageLength: null }
```
Morgana persists this and uses it to decide whether to adapt (degrade) outbound messages. Since Cauldron supports everything, the `AdaptingChannelService` short-circuits without calling the LLM.

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Cauldron:MorganaURL` | Morgana backend base URL for REST + SignalR (default `https://localhost:5001`) |
| `Cauldron:Authentication:SymmetricKey` | Shared HMAC key matching Morgana's `Issuers[].SymmetricKey` for `Name=cauldron` |
| `Cauldron:Authentication:Issuer` | Token issuer (default `cauldron`) |
| `Cauldron:Authentication:Audience` | Token audience (default `morgana.ai`) |
| `Cauldron:AgentExitMessage` | Template for the courtesy line injected when a specialised agent completes (default `"{0} has completed its spell. I'm back to you!"`; `{0}` is the agent's display name). Mirrors Rune's `Rune:AgentExitMessage`. |
| `Cauldron:StreamingResponse:TypewriterTickMilliseconds` | Typewriter speed (default `15`ms) |
| `Cauldron:StreamingResponse:TypewriterTickChars` | Chars per tick (default `1`) |
| `Cauldron:LandingMessages` | String array of whimsical "warming up" lines for the sparkle loader, picked uniformly at random per session. Mirrors Rune's `Rune:LandingMessages` (same pool, same intent). |
| `Cauldron:Widget:AllowedEmbedOrigins` | Origins allowed to frame Cauldron, emitted as CSP `frame-ancestors`. Empty (the default) means `'self'` only: no external site can embed the widget until its origin is listed. |

## Embeddable Widget

`Widget` is a static-asset Razor Class Library — no C#, no Razor, no server of its own. `Cauldron.csproj` references it, which publishes its `wwwroot/` under `/widget/`; the dependency points that way because the widget has to be **served by** the Cauldron instance it embeds.

A host site of any technology (JSP, PHP, static HTML — the contract is plain browser HTML) integrates it with one tag and no parameters:

```html
<script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>
```

`morgana-widget.js` reads its own `src` to learn the Cauldron origin, which is what buys the zero-parameter contract. It mounts a closed shadow root (so host-page CSS and widget CSS cannot reach each other) holding a launcher pill — Morgana's gif plus *Consult Morgana* — that toggles a panel containing a sandboxed `<iframe>` pointed at Cauldron's chat page.

Two lifecycle rules matter, both dictated by Blazor Server: the iframe is created on **first open**, because loading Cauldron opens a circuit and pins per-visitor state on the server; and it is **never destroyed**, because that circuit is the conversation, so closing hides the panel instead of unmounting. Zero JS dependencies is deliberate — a widget cannot know the stack of the page it lands in.

`/widget/morgana.html` is a mock third-party page — a fictitious plant nursery — carrying only the script tag; it works out of the box because the default `frame-ancestors 'self'` covers same-origin framing. It is styled with **inline attributes only**: no stylesheet, no class name, no custom property, so there is nothing on the page that could collide with the widget's own CSS or inherit into its shadow root. The one thing that still crosses the boundary is plain inheritance from `<body>`, which is exactly what the widget's `:host` block pins — and what the page's serif type is there to put under pressure.

## UI Patterns

- **Agent-specific theming**: border colors, CSS classes, and header animations change based on `CurrentAgentName` (base Morgana vs specialized agent)
- **Quick reply gating**: textarea and send button are disabled while unselected quick replies or typing indicators are active
- **Completion messages**: when an agent signals `AgentCompleted = true`, a presentation-style transition message is injected ("Morgana is back")
- **History boundaries**: on resume, the lifecycle service detects agent-turn-boundary transitions and injects synthetic completion messages for visual continuity
- **Markdown rendering**: `MarkdownRendererService` uses Markdig to convert message text to HTML, rendered via `@MarkdownRendererService.ToHtml()`
- **Rich cards**: `RichCardComponent.razor` dispatches on the `type` discriminator to the appropriate sub-renderer (8 types)

## Build and Run

- **Target**: .NET 10, Blazor Server
- **Build**: `dotnet build` from `Channels/Cauldron/` directory
- **Run**: `dotnet run` — default https://localhost:5002 (requires Morgana backend running)
- **Docker**: `Channels/Cauldron/Cauldron.Dockerfile`

## Conventions

- All behavioral concerns are behind interfaces (`IChatStateService`, `IConversationLifecycleService`, `IStreamingService`, etc.)
- `SignalRService` is the only component that touches SignalR directly; everything else subscribes to events
- Error display: transient errors → `FadingMessage` banners with auto-dismiss; critical errors → persistent chat messages
- `ProtectedLocalStorage` for conversation ID persistence (AES-encrypted by ASP.NET Core)
- Server is source of truth for final message text (streaming chunks are progressive preview only)
