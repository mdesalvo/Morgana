# Cauldron — Morgana's reference frontend

## What is Cauldron

A **Blazor Server** application, the reference channel for Morgana: rich chat UI with streaming,
quick replies, rich cards, typing indicators and conversation persistence across browser sessions.
REST for the conversation lifecycle, SignalR for delivery.

It occupies the **rich-Web** quadrant of the channel matrix; Grimoire holds rich-TTY, Rune holds
poor-TTY. It lives at `Channels/Cauldron/`, its own solution.

## Layout

`Services/` holds one implementation per `Interfaces/` contract — lifecycle, chat state, streaming,
storage, history, landing message, markdown. `Components/` holds the Razor components, of which the
`RichCard*` family is one per card `type` discriminator, dispatched by `RichCardComponent.razor`.
`Messages/` holds the two shapes that are **not** wire contracts. `Widget/` is a static-asset RCL,
described below.

## Wire contracts: nothing is duplicated

Cauldron takes a `ProjectReference` on **`Morgana.Contracts`** and consumes its types directly —
requests *and* responses, the very types `MorganaController` returns. There is no hand-written
mirror to keep in lockstep: change a contract once, in `Morgana.Contracts`.

They are immutable records, so code that once mutated one in place rebuilds with `with` expressions.

**`MorganaChatMessage` is a wire DTO, not the UI model.** `ConversationLifecycleService.MapToChatMessage`
projects it onto `Messages/ChatMessage.cs`, which adds state the server knows nothing about (typing
indicator, streaming flag, selected quick reply) and has its own richer `MessageType`. **Keep the two
enums apart**: the mapping switch is exhaustive on purpose, so a value added server-side breaks the
build here instead of silently landing on the wrong styling.

Channel identity lives channel-side, in `Messages/CauldronChannelMetadata.cs`, never on the shared
contract. The Dockerfile mirrors the repo layout under `/src` so the project reference resolves.

## Channel handshake

```csharp
Coordinates  = { ChannelName = "cauldron", DeliveryMode = "signalr" }
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true,
                 SupportsStreaming: true, SupportsMarkdown: true, MaxMessageLength: null }
```

Because Cauldron declares everything, `AdaptingChannelService` short-circuits without calling the
LLM. Exercising the degradation path is Rune's job.

## Authentication

`MorganaAuthHandler` is a `DelegatingHandler` minting short-lived JWTs: HMAC-SHA256 with the key from
`Cauldron:Authentication:SymmetricKey`, issuer `cauldron`, audience `morgana.ai`, five minutes,
regenerated per request. Used by the named `HttpClient` through the handler pipeline and by
`SignalRService` through its `AccessTokenProvider`.

**Onboarding a fresh Morgana instance:**
1. Add `{ "Name": "cauldron", "SymmetricKey": "<≥256 bit, base64>" }` to `Morgana:Authentication:Issuers[]`.
   That list holds **channels and nothing else**: the key buys the conversation API (REST plus
   SignalR) and nothing published under `/a2a`, which only a declared partner reaches
2. Put the same key under `Cauldron:Authentication:SymmetricKey` through user-secrets or an
   environment variable, never a commit
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Cauldron/` (`:5002`)

## Streaming

First chunk removes the typing indicator, creates a streaming `ChatMessage` and starts the typewriter
timer; each tick consumes N chars from the buffer. **Finalization overwrites the text with the
server-authoritative version**, which may differ from what was streamed if the channel adapter
rewrote the message — the server is the source of truth, streamed chunks are progressive preview.

## The embeddable widget

`Widget` is a static-asset Razor Class Library — no C#, no Razor, no server of its own. Cauldron
references it, publishing its assets under `/widget/`; the dependency points that way because the
widget must be **served by** the Cauldron instance it embeds.

A host site of any technology integrates it with one tag and no parameters:

```html
<script src="https://your-cauldron-host/widget/morgana-widget.js" defer></script>
```

`morgana-widget.js` reads its own `src` to learn the Cauldron origin, which is what buys the
zero-parameter contract. It mounts a **closed** shadow root so host-page CSS and widget CSS cannot
reach each other, holding a launcher pill that toggles a panel with a sandboxed `<iframe>`.

**Two lifecycle rules, both dictated by Blazor Server**: the iframe is created on **first open**,
because loading Cauldron opens a circuit and pins per-visitor state on the server; it is **never
destroyed**, because that circuit *is* the conversation, so closing hides the panel instead of
unmounting. Zero JS dependencies is deliberate — a widget cannot know the stack of the page it lands
in.

`/widget/morgana.html` is a mock third-party page carrying only the script tag, excluded from
**Release** builds along with `demo/`: a published image serves the launcher alone, since a customer
hosts the widget on their own origin where a fictitious nursery does not belong. It is styled with
**inline attributes only** — no stylesheet, no class name, no custom property — so nothing on it can
collide with the widget's CSS or inherit into its shadow root.

## Key configuration

| Section | Purpose |
|---|---|
| `Cauldron:MorganaURL` | Backend base URL for REST plus SignalR (default `https://localhost:5001`) |
| `Cauldron:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=cauldron`, plus `Issuer` and `Audience` |
| `Cauldron:AgentExitMessage` | The courtesy line injected when a specialised agent completes; `{0}` is its display name. Mirrors Rune's |
| `Cauldron:StreamingResponse:*` | `TypewriterTickMilliseconds` (15), `TypewriterTickChars` (1) |
| `Cauldron:LandingMessages` | The "warming up" lines for the sparkle loader, picked at random per session |
| `Cauldron:Widget:AllowedEmbedOrigins` | Origins allowed to frame Cauldron, emitted as CSP `frame-ancestors`. Empty (the default) means `'self'` only: **no external site can embed the widget until its origin is listed** |

## UI patterns

- **Agent theming**: borders, CSS classes and header animations follow `CurrentAgentName` — base
  Morgana against a specialised agent
- **Quick reply gating**: textarea and send button are disabled while unselected quick replies or a
  typing indicator are active
- **Completion messages**: `AgentCompleted = true` injects a transition message; on resume, the
  lifecycle service detects turn boundaries in the history and injects the same synthetically

## Build and Run

- **Target**: .NET 10, Blazor Server
- **Build/Run**: from `Channels/Cauldron/` — `dotnet run`, https://localhost:5002, backend required
- **Docker**: `Cauldron.Dockerfile`

## Conventions

- Behavioral concerns behind interfaces, default implementation alongside, registration in `Program.cs`
- `SignalRService` is the only component touching SignalR directly; everything else subscribes to events
- Transient errors become `FadingMessage` banners; critical ones become persistent chat messages
- Conversation ID persists through `ProtectedLocalStorage`, encrypted by ASP.NET Core
