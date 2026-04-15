# Cauldron - Morgana's Reference Frontend

## What is Cauldron

Cauldron is a **Blazor Server** web application that serves as the reference channel for Morgana. It provides a rich chat UI with real-time streaming, quick reply buttons, rich cards, typing indicators, and conversation persistence across browser sessions. Communicates with the Morgana backend via REST API (conversation lifecycle, message sending) and SignalR (real-time message delivery, streaming chunks).

Cauldron lives at `Cauldron/` in the repo root, separate from the `Morgana/` working directory.

## Project Structure

```
Cauldron/
  Pages/Index.razor              # Main chat page (single-page app)
  Components/                    # Reusable Blazor components
    RichCard.razor               # Rich card container (dispatches to sub-renderers)
    RichCardTextBlock.razor      # text_block component
    RichCardKeyValue.razor       # key_value component
    RichCardDivider.razor        # divider component
    RichCardList.razor           # list component
    RichCardSection.razor        # section component
    RichCardGrid.razor           # grid component
    RichCardBadge.razor          # badge component
    RichCardImage.razor          # image component
    RichCardComponent.razor      # Polymorphic dispatcher (type discriminator → sub-renderer)
    QuickReplyButton.razor       # Quick reply button row
    FadingMessage.razor          # Temporary system warning banners (auto-dismiss)
    SendButton.razor             # Animated send button with agent-specific styling
    ConfirmModal.razor           # Modal dialog for "New Conversation" confirmation
  Handlers/
    MorganaAuthHandler.cs        # DelegatingHandler: self-issues JWT tokens for Morgana API auth
  Interfaces/
    IChatStateService.cs         # Chat UI state management contract
    IConversationLifecycleService.cs  # Start/resume/clear conversation contract
    IConversationStorageService.cs    # Browser localStorage persistence contract
    IConversationHistoryService.cs    # History fetch contract
    ILandingMessageService.cs         # Welcome message contract
    IStreamingService.cs              # Streaming lifecycle contract
  Services/
    SignalRService.cs            # SignalR client: connection, events, auto-reconnect
    ConversationLifecycleService.cs   # Orchestrates REST API + SignalR + storage
    ChatStateService.cs          # In-memory UI state (messages, agent, sending, etc.)
    StreamingService.cs          # Chunk buffering, typewriter timer, finalization
    ProtectedLocalStorageService.cs   # IConversationStorageService → ProtectedLocalStorage
    ConversationHistoryService.cs     # GET api/morgana/conversation/{id}/history
    LandingMessageService.cs     # Random welcome message during sparkle loader
    MarkdownRendererService.cs   # Markdig-based HTML rendering for message text
  Messages/
    ChatMessage.cs               # UI-side message model (text, role, agent, quickReplies, richCard, streaming state)
    Contracts/                   # DTOs mirroring Morgana's wire format
      ChannelMessage.cs          # SignalR "ReceiveMessage" payload
      ChannelMetadata.cs         # Handshake metadata (with Cauldron singleton)
      ChannelCapabilities.cs     # Feature flags (richCards, quickReplies, streaming, markdown, maxLength)
      QuickReply.cs              # Quick reply button definition (id, label, value)
      RichCard.cs                # Rich card with polymorphic CardComponent array
    ConversationStartResponse.cs
    ConversationResumeResponse.cs
    ConversationHistoryResponse.cs
  Program.cs                     # DI wiring and app pipeline
  Shared/MainLayout.razor        # Layout wrapper
  wwwroot/css/                   # Component-specific CSS (site.css, rich-card.css, quick-reply.css, etc.)
  wwwroot/images/                # Morgana avatar images
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
- Algorithm: HMAC-SHA256 with shared symmetric key from `Cauldron:Authentication:SymmetricKey`
- Issuer: `cauldron` (must be in Morgana's `ValidIssuers` list)
- Subject: `cauldron-app`
- Audience: `morgana.ai`
- Lifetime: 5 minutes (re-generated per request)

Used by both the named `HttpClient` (automatic via handler pipeline) and `SignalRService` (via `AccessTokenProvider` callback in hub connection builder).

## Contract Duplication

DTOs in `Messages/Contracts/` are **duplicated** from Morgana's `Records.cs` — there is no shared contracts project. When modifying these types, both sides must be updated in lockstep:
- `ChannelMessage` ↔ `Records.ChannelMessage`
- `ChannelMetadata` ↔ `Records.ChannelMetadata`
- `ChannelCapabilities` ↔ `Records.ChannelCapabilities`
- `QuickReply` ↔ `Records.QuickReply`
- `RichCard` / `CardComponent` ↔ `Records.RichCard` / `Records.CardComponent` (with JSON polymorphic `type` discriminator)

## Channel Handshake

At conversation start, Cauldron announces itself via `ChannelMetadata.Cauldron` singleton:
```csharp
ChannelName = "cauldron"
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true,
                 SupportsStreaming: true, SupportsMarkdown: true,
                 MaxMessageLength: null }
```
Morgana persists this and uses it to decide whether to adapt (degrade) outbound messages. Since Cauldron supports everything, the `AdaptingChannelService` short-circuits without calling the LLM.

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Cauldron:MorganaURL` | Morgana backend base URL for REST + SignalR |
| `Cauldron:Authentication:SymmetricKey` | Shared HMAC key (must match Morgana's) |
| `Cauldron:Authentication:Issuer` | Token issuer (default `cauldron`) |
| `Cauldron:Authentication:Audience` | Token audience (default `morgana.ai`) |
| `Cauldron:StreamingResponse:TypewriterTickMilliseconds` | Typewriter speed (default 15ms) |
| `Cauldron:StreamingResponse:TypewriterTickChars` | Chars per tick (default 1) |
| `Cauldron:LandingMessages` | Array of random welcome messages for sparkle loader |

## UI Patterns

- **Agent-specific theming**: border colors, CSS classes, and header animations change based on `CurrentAgentName` (base Morgana vs specialized agent)
- **Quick reply gating**: textarea and send button are disabled while unselected quick replies or typing indicators are active
- **Completion messages**: when an agent signals `AgentCompleted = true`, a presentation-style transition message is injected ("Morgana is back")
- **History boundaries**: on resume, the lifecycle service detects agent-turn-boundary transitions and injects synthetic completion messages for visual continuity
- **Markdown rendering**: `MarkdownRendererService` uses Markdig to convert message text to HTML, rendered via `@MarkdownRendererService.ToHtml()`
- **Rich cards**: `RichCardComponent.razor` dispatches on the `type` discriminator to the appropriate sub-renderer (8 types)

## Build and Run

- **Target**: .NET 10, Blazor Server
- **Build**: `dotnet build` from `Cauldron/` directory
- **Run**: `dotnet run` — default https://localhost:7172 (requires Morgana backend running)
- **Docker**: `Cauldron.Dockerfile`

## Conventions

- All behavioral concerns are behind interfaces (`IChatStateService`, `IConversationLifecycleService`, `IStreamingService`, etc.)
- `SignalRService` is the only component that touches SignalR directly; everything else subscribes to events
- Error display: transient errors → `FadingMessage` banners with auto-dismiss; critical errors → persistent chat messages
- `ProtectedLocalStorage` for conversation ID persistence (AES-encrypted by ASP.NET Core)
- Server is source of truth for final message text (streaming chunks are progressive preview only)
