# Rune - Morgana's Command-Line Webhook Channel

## What is Rune

Rune is a minimal **.NET 10 console application** that serves as the second reference channel for Morgana, alongside Cauldron. Where Cauldron is the rich, best-case frontend (SignalR, streaming, rich cards, quick replies, markdown — every expressive feature on), Rune is deliberately **poor but honest**: a 200-character hard limit, no rich cards, no quick replies, no streaming, no markdown. Its purpose is to exercise Morgana's capability-degradation path on every turn — the channel-adapter rewrite, the streaming suppression, the webhook delivery mode — so that code cannot silently rot.

Rune lives at `Channels/Rune/` in the repo root, alongside other reference channels, separate from the `Morgana/` working directory.

## Why "poor but honest"

A second rich channel would be a fast-path twin of Cauldron, validating nothing that is not already exercised. Rune instead declares a tight capability budget that forces `MorganaChannelAdapter.AdaptAsync` to rewrite almost every outbound message and forces the streaming path to be suppressed upstream. At the same time Rune is **not a rogue echo client**: it self-issues its own JWTs under `iss=rune` with its own `SymmetricKey`, so the per-issuer auth gate is also closed end-to-end by a second channel identity.

## Project Structure

```
Channels/Rune/
  Program.cs                          # Entry point: Kestrel + DI + lifecycle
  Rune.csproj                         # .NET 10 Web SDK, deps: Spectre.Console, JsonWebTokens
  Rune.slnx                           # Solution (sibling to Cauldron.slnx)
  Directory.Build.props               # Shared build/version metadata (0.21.0 aligned)
  appsettings.json                    # Morgana URL, callback URL, auth config
  Properties/launchSettings.json      # Dev profile: https://localhost:5003
  Rune.Dockerfile                     # Multi-stage container build (root context)
  Handlers/
    MorganaAuthHandler.cs             # DelegatingHandler: self-issues JWT for outbound calls
  Messages/                           # Response DTOs that mirror anonymous controller shapes
    StartConversationResponse.cs      # Echo of conversation id from MorganaController.StartConversation
  Messages/Contracts/                 # DTOs duplicated in lockstep from Morgana.AI.Records
    ChannelMessage.cs                 # Inbound webhook payload
    ChannelMetadata.cs                # Handshake metadata (with Rune.Build(callbackUrl) factory)
    ChannelCoordinates.cs             # Identity + addressing (channelName, deliveryMode, callbackUrl)
    ChannelCapabilities.cs            # Feature flags (all false for Rune, MaxMessageLength=200)
    StartConversationRequest.cs       # conversation/start body (conversationId + channelMetadata)
    SendMessageRequest.cs             # conversation/{id}/message body (conversationId + text)
    QuickReply.cs                     # Kept for binary compat (stripped by adapter)
    RichCard.cs                       # Kept for binary compat (stripped by adapter)
  Services/
    MorganaClient.cs                  # REST wrapper: start / send / end conversation
    WebhookReceiver.cs                # Thin dispatcher, OnMessage delegate wired in Program.cs
    ConsoleUi.cs                      # Spectre.Console Live(Layout) — sticky header + REPL body
```

## Architecture

### Communication with Morgana

```
Rune   ──REST──────→ Morgana.Web (MorganaController)  # outbound: start/send/end
       ←─webhook POST── Morgana.Web (WebhookChannelService) # inbound: ChannelMessage on /morgana-hook
```

- **Outbound REST** (via `HttpClient` named "Morgana", base address `Rune:MorganaURL`): conversation start/send/end, authenticated by a self-issued JWT injected through `MorganaAuthHandler`.
- **Inbound webhook** (via Kestrel on port 5003, endpoint `POST /morgana-hook`): Morgana POSTs a serialized `ChannelMessage` on every outbound turn; `WebhookReceiver.Dispatch` hands it to `ConsoleUi.EnqueueIncoming` via a delegate wired in `Program.cs` (breaks the circular DI between receiver and UI).

### DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `MorganaAuthHandler` | Transient | JWT token generation for outbound REST auth |
| `HttpClient` "Morgana" | Named | REST API calls with auto Bearer token injection |
| `MorganaClient` | Singleton | Start/send/end conversation wrapper |
| `WebhookReceiver` | Singleton | Minimal-API dispatcher, settable `OnMessage` callback |
| `ConsoleUi` | Singleton | Spectre.Console Live UI (one Rune session per process) |

### Lifecycle (Program.cs)

1. `builder.Logging.ClearProviders()` — silence Kestrel / ASP.NET Core logs (they corrupt the Live TUI)
2. `app.StartAsync()` — Kestrel listens on `https://localhost:5003` (dev) / `http://+:5003` (container)
3. `webhook.OnMessage = ui.EnqueueIncoming` — wire inbound to UI
4. `morganaClient.StartConversationAsync()` — handshake with `ChannelMetadata.Build(callbackUrl)` → returns conversationId
5. `ui.RunAsync(conversationId, onSend)` — blocks on the Live loop until `/quit` / `Esc`
6. `finally { morganaClient.EndConversationAsync(); await app.StopAsync(); }`

## Channel Handshake

At conversation start, Rune announces itself via `ChannelMetadata.Build(callbackUrl)`:
```csharp
Coordinates  = { ChannelName = "rune", DeliveryMode = "webhook", CallbackUrl = "<from Rune:CallbackURL>" }
Capabilities = { SupportsRichCards: false, SupportsQuickReplies: false,
                 SupportsStreaming: false, SupportsMarkdown: false,
                 MaxMessageLength: 200 }
```

Morgana's controller gate additionally requires `callbackUrl` to be an absolute URI when `deliveryMode=webhook` — enforced at handshake, fail-closed. The `MaxMessageLength=200` below `Morgana:AdaptiveMessaging:RichFeaturesMinLength` also forces rich / quick-replies off on the server side even if a future version of Rune were to claim them.

## Authentication

`MorganaAuthHandler` is a `DelegatingHandler` that generates short-lived JWT tokens:
- **Algorithm**: HMAC-SHA256 with shared symmetric key from `Rune:Authentication:SymmetricKey`
- **Issuer**: `rune` — must be present in Morgana's `Morgana:Authentication:Issuers[]` list with a matching `SymmetricKey`; unknown issuers are rejected at the Morgana gate
- **Subject**: `rune-app`
- **Audience**: `morgana.ai`
- **Lifetime**: 5 minutes (re-generated per request)

**Trust model is asymmetric by design**: Rune signs its outbound calls toward Morgana; Morgana does **not** sign the inbound webhook POST toward Rune. This matches `WebhookChannelService`'s convention (GitHub / Stripe / Twilio style) and is not a gap.

**Onboarding checklist for a fresh Morgana instance:**
1. Add an entry to `Morgana:Authentication:Issuers[]` in the destination Morgana configuration: `{ "Name": "rune", "SymmetricKey": "<at least 256 bit, base64>" }`
2. Put the same `SymmetricKey` under `Rune:Authentication:SymmetricKey` via user-secrets or env var (never commit)
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Rune/` (`:5003`)

## Contract Duplication

DTOs in `Messages/Contracts/` are **duplicated** from Morgana's `Records.cs` — there is no shared contracts project. When modifying these types, both sides must be updated in lockstep:
- `ChannelMessage` ↔ `Records.ChannelMessage`
- `ChannelMetadata` ↔ `Records.ChannelMetadata`
- `ChannelCoordinates` ↔ `Records.ChannelCoordinates` (including `CallbackUrl`)
- `ChannelCapabilities` ↔ `Records.ChannelCapabilities`
- `QuickReply` ↔ `Records.QuickReply` (kept for binary compat even though Rune doesn't render them)
- `RichCard` / `CardComponent` ↔ `Records.RichCard` / `Records.CardComponent` (kept for binary compat even though Rune doesn't render them)
- `StartConversationRequest` ↔ `Records.StartConversationRequest`
- `SendMessageRequest` ↔ `Records.SendMessageRequest`

`StartConversationResponse` lives under `Messages/` (not `Messages/Contracts/`) because it mirrors an anonymous response shape in `MorganaController.StartConversation`, not a declared record — same split Cauldron uses for its own response DTOs.

## Terminal UI (ConsoleUi)

Built on Spectre.Console's `LiveDisplay` + `Layout`:

- **Header** (sticky, 3 rows): panel `Rune → Morgana` with the current speaker name colored by role and a truncated conversation id.
- **Body** (scrolling): chat history with each line colored by speaker, plus a bottom-most input line with a blinking cursor.

### Colors (dark-theme palette)

| Who | Color | Rationale |
|---|---|---|
| `Morgana` | `magenta1` | Base assistant identity |
| `Morgana (Agent)` | `hotpink` | Specialized agent (billing, contract, …) — `AgentName` derived from the wire contract |
| `You` | `skyblue1` | User input and committed messages |

### Input handling

- `Console.ReadKey(intercept: true)` on a background task, polling `Console.KeyAvailable` every 25 ms (Spectre.Console's Live rendering cannot share stdin with a first-class prompt).
- **Enter** — commits the current buffer: if it equals `/quit` the UI exits; otherwise it's appended to history as `You: …` and sent via `onSend`.
- **Backspace** — deletes the last character from the buffer.
- **Esc** — immediate exit.
- **Other printable chars** — appended to the buffer; layout refreshed on each keystroke.

### Resume

No resume in v1. Every Rune process start begins a fresh conversation. Keep this explicit: if future Rune picks up a conversation id from some store, the `ChannelMetadata.Build` handshake must also be re-announced (Morgana's `ConversationManagerActor` re-persists channel metadata on resume).

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Rune:MorganaURL` | Morgana backend base URL for outbound REST (default `https://localhost:5001`) |
| `Rune:CallbackURL` | Absolute URL Morgana POSTs inbound messages to (default `https://localhost:5003/morgana-hook`) |
| `Rune:Authentication:SymmetricKey` | Shared HMAC key matching Morgana's `Issuers[].SymmetricKey` for `Name=rune` |
| `Rune:Authentication:Issuer` | Token issuer (default `rune`) |
| `Rune:Authentication:Audience` | Token audience (default `morgana.ai`) |

## Build and Run

- **Target**: .NET 10, console app hosted on Kestrel (via `Microsoft.NET.Sdk.Web`)
- **Build**: `dotnet build` from `Channels/Rune/` directory
- **Run**: `dotnet run` — default `https://localhost:5003` for the webhook listener (requires Morgana backend running and the `rune` issuer onboarded)
- **Docker**: `Channels/Rune/Rune.Dockerfile` (context is the repo root, mirroring the Morgana / Cauldron pattern). Rune is **not** launched by `docker compose up` — the Spectre.Console Live UI needs to own the terminal, so it must be started interactively in a separate terminal after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports rune
  ```
  The `run --service-ports` invocation allocates the TTY Spectre requires *and* publishes `5003:5003` so Morgana's webhook callback can reach Rune's listener. The compose file sets `stdin_open: true` + `tty: true` on the `rune` service to keep this flow explicit.

## Conventions

- **Logging is silenced** at startup — the Spectre.Console Live UI owns the terminal; errors surface as red in-UI system lines.
- **Asymmetric trust** is a first-class design choice, not a bug — do not introduce webhook signing without revisiting the decision recorded in the `WebhookChannelService` notes.
- **Singletons** — one Rune process == one Rune session; if multi-session ever becomes a goal, move state (`ConsoleUi.history`, `WebhookReceiver.OnMessage`) onto a per-conversation scope first.
- **Server is source of truth** for final message text — even though Rune suppresses streaming, it still defers to whatever Morgana's channel adapter decides to send.
