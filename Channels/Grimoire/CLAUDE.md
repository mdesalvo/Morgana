# Grimoire - Morgana's Rich-TTY Webhook Channel

## What is Grimoire

Grimoire is a minimal **.NET 10 console application** that serves as the rich-TTY reference channel for Morgana, sibling to Cauldron. Where Cauldron renders Morgana's full expressive surface in HTML (SignalR, streaming, rich cards, quick replies, markdown), Grimoire renders the **same** full profile inside a Spectre.Console terminal UI: every expressive feature on, no message-length cap, content arrives integral and is Spectrized locally. It is the **textual Cauldron** — what a power user sees when their workflow lives in the terminal.

Grimoire lives at `Channels/Grimoire/` in the repo root, alongside Cauldron and Rune. Rune occupies the matrix's complementary cell (TTY-poor: 500-char cap, no rich features, exercises Morgana's degradation path); Grimoire is its rich sibling, and the two never run at the same time — only one can own stdin/stdout.

## Why "textual Cauldron"

A pure-text channel doesn't have to be poor. Grimoire's role is to demonstrate that Morgana's content layer is channel-renderer-agnostic: the same rich card schema, the same streaming chunks, the same quick replies all land at Grimoire intact and turn into Spectre primitives (panels, tables, rules, trees, selection prompts). It also closes the channels × capability matrix that Cauldron and Rune leave half-open:

|             | HTML        | TTY                |
|-------------|-------------|--------------------|
| **Full**    | Cauldron    | **Grimoire**       |
| **Poor**    | —           | Rune               |

Grimoire is **not a Cauldron fork**. It is its own channel identity: self-issued JWTs under `iss=grimoire` with its own `SymmetricKey`, its own Kestrel port (`5004`), and a direct project reference to the shared `Morgana.Contracts` wire package. The per-issuer auth gate is closed end-to-end by a third channel identity, independent of Cauldron and Rune.

## Project Structure

```
Channels/Grimoire/
  Program.cs                         # Entry point: Kestrel + DI + lifecycle
  Grimoire.csproj                    # .NET 10 Web SDK, deps: Spectre.Console, JsonWebTokens
  Grimoire.slnx                      # Solution (sibling to Cauldron.slnx / Rune.slnx)
  Directory.Build.props              # Shared build/version metadata
  Directory.Build.targets            # MSBuild target that regenerates root .env.versions on each build
  appsettings.json                   # Morgana URL, callback URL, auth, typewriter cadence
  Properties/launchSettings.json     # Dev profile: https://localhost:5004
  Grimoire.Dockerfile                # Multi-stage container build (root context)
  Handlers/
    MorganaAuthHandler.cs            # DelegatingHandler: self-issues JWT for outbound calls
  Interfaces/
    IViewportResizeWatcher.cs        # Abstraction over terminal resize notifications (SIGWINCH vs polling)
  Messages/                          # Channel-only shapes (the shared wire DTOs come from Morgana.Contracts)
    StartConversationResponse.cs     # Echo of conversation id from MorganaController.StartConversation
    GrimoireChannelMetadata.cs       # Build(callbackUrl) factory over Morgana.Contracts.ChannelMetadata (channel identity)
  Services/
    MorganaClientService.cs          # REST wrapper: start / send / end conversation
    WebhookReceiverService.cs        # Thin dispatcher, OnMessage + OnChunk delegates wired in Program.cs
    ConsoleUiService.cs              # Spectre.Console Live(Layout) — sticky header, history, live streaming pane, REPL prompt
    LandingMessageService.cs         # Random startup line from Grimoire:LandingMessages pool
    PollingResizeWatcherService.cs   # IViewportResizeWatcher impl: cross-platform Console.WindowWidth/Height polling
    SigWinchResizeWatcherService.cs  # IViewportResizeWatcher impl: Linux/macOS SIGWINCH-driven (no polling overhead)
```

## Architecture

### Communication with Morgana

```
Grimoire   ──REST──────→ Morgana.Web (MorganaController)        # outbound: start/send/end
       ←─webhook POST── Morgana.Web (WebhookChannelService)     # inbound:  ChannelMessage on /morgana-hook
       ←─webhook POST── Morgana.Web (WebhookChannelService)     # inbound:  StreamChunkRequest on /morgana-hook/chunk
```

- **Outbound REST** (via `HttpClient` named "Morgana", base address `Grimoire:MorganaURL`): conversation start/send/end, authenticated by a self-issued JWT injected through `MorganaAuthHandler`.
- **Inbound webhook** (via Kestrel on port 5004): two endpoints, both POST.
  - `/morgana-hook` — Morgana POSTs a serialized `ChannelMessage` for every final outbound turn.
  - `/morgana-hook/chunk` — Morgana POSTs `StreamChunkRequest` deltas while an agent streams its response.
  - `WebhookReceiver.Dispatch` / `DispatchChunk` hand the payloads to `ConsoleUi.EnqueueIncoming` / `EnqueueChunk` via delegates wired in `Program.cs` (breaks the circular DI between receiver and UI).

### DI Registrations (Program.cs)

| Registration | Type | Purpose |
|---|---|---|
| `MorganaAuthHandler` | Transient | JWT token generation for outbound REST auth |
| `HttpClient` "Morgana" | Named | REST API calls with auto Bearer token injection |
| `MorganaClientService` | Singleton | Start/send/end conversation wrapper |
| `WebhookReceiverService` | Singleton | Minimal-API dispatcher, settable `OnMessage` / `OnChunk` callbacks |
| `ConsoleUiService` | Singleton | Spectre.Console Live UI (one Grimoire session per process) |
| `LandingMessageService` | Singleton | Picks a random "warming up" line from the `Grimoire:LandingMessages` pool |
| `IViewportResizeWatcher` | Singleton | OS-specific resize watcher: `SigWinchResizeWatcherService` on Linux/macOS, `PollingResizeWatcherService` elsewhere — selected at startup in `Program.cs` |

### Lifecycle (Program.cs)

1. `builder.Logging.ClearProviders()` — silence Kestrel / ASP.NET Core logs (they corrupt the Live TUI)
2. `app.StartAsync()` — Kestrel listens on `https://localhost:5004` (dev) / `http://+:5004` (container)
3. `webhook.OnMessage = uiService.EnqueueIncoming` + `webhook.OnChunk = uiService.EnqueueChunk` — wire inbound to UI
4. `morganaClientService.StartConversationAsync()` — handshake with `ChannelMetadata.Build(callbackUrl)` → returns conversationId
5. `uiService.RunAsync(conversationId, onSend)` — blocks on the Live loop until `/quit` / `Esc`
6. `finally { morganaClientService.EndConversationAsync(); await app.StopAsync(); }`

## Channel Handshake

At conversation start, Grimoire announces itself via `ChannelMetadata.Build(callbackUrl)`:
```csharp
Coordinates  = { ChannelName = "grimoire", DeliveryMode = "webhook", CallbackUrl = "<from Grimoire:CallbackURL>" }
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true, SupportsStreaming: true, SupportsMarkdown: true,
                 MaxMessageLength: null }
```

Morgana's controller gate additionally requires `callbackUrl` to be an absolute URI when `deliveryMode=webhook` — enforced at handshake, fail-closed. The full capability profile means `MorganaChannelAdapter.AdaptAsync` short-circuits on every turn (`FitsWithin` returns true), the streaming path is **not** suppressed upstream, and the wire payload reaches Grimoire integral.

## Authentication

`MorganaAuthHandler` is a `DelegatingHandler` that generates short-lived JWT tokens:
- **Algorithm**: HMAC-SHA256 with shared symmetric key from `Grimoire:Authentication:SymmetricKey`
- **Issuer**: `grimoire` — must be present in Morgana's `Morgana:Authentication:Issuers[]` list with a matching `SymmetricKey`; unknown issuers are rejected at the Morgana gate
- **Subject**: `grimoire-app`
- **Audience**: `morgana.ai`
- **Lifetime**: 5 minutes (re-generated per request)

**Trust model is asymmetric by design**: Grimoire signs its outbound calls toward Morgana; Morgana does **not** sign the inbound webhook POST toward Grimoire. This matches `WebhookChannelService`'s convention (GitHub / Stripe / Twilio style) and is not a gap.

**Onboarding checklist for a fresh Morgana instance:**
1. Add an entry to `Morgana:Authentication:Issuers[]` in the destination Morgana configuration: `{ "Name": "grimoire", "SymmetricKey": "<at least 256 bit, base64>" }`
2. Put the same `SymmetricKey` under `Grimoire:Authentication:SymmetricKey` via user-secrets or env var (never commit)
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Grimoire/` (`:5004`)

## Wire Contracts (shared project)

The wire DTOs (`ChannelMessage`, `ChannelMetadata`, `ChannelCoordinates` incl. `CallbackUrl`, `ChannelCapabilities`, `QuickReply`, `RichCard`/`CardComponent`, `StartConversationRequest`, `SendMessageRequest`, `StreamChunkRequest`) are **no longer duplicated**: Grimoire takes a direct `ProjectReference` to **`Morgana.Contracts`** (`..\..\Morgana\Morgana.Contracts\Morgana.Contracts.csproj`) — the single source of truth shared with Morgana.AI — and consumes them under the `Morgana.Contracts` namespace. Change a contract once, in `Morgana.Contracts`. `StreamChunkRequest` (the `{callbackUrl}/chunk` webhook body) now lives in `Morgana.Contracts` too and is consumed directly by Morgana.Web's `WebhookChannelService`, so there is no longer a private server-side copy to keep in lockstep.

The contract types are immutable records (init-only / positional): `StartConversationRequest`/`SendMessageRequest` are constructed positionally, and `QuickReply.Termination` is now `bool?`. Channel identity lives channel-side in `Messages/GrimoireChannelMetadata.cs` (`GrimoireChannelMetadata.Build(callbackUrl)`), not on the shared contract.

The Docker build mirrors the repo layout under `/src` and stages the `Morgana.Contracts` subtree so the `ProjectReference` resolves (see `Grimoire.Dockerfile`).

`StartConversationResponse` stays under `Messages/` because it mirrors an anonymous response shape in `MorganaController.StartConversation`, not a declared contract (same split Cauldron uses for its own response DTOs).

## Terminal UI (ConsoleUiService)

Built on Spectre.Console's `LiveDisplay` + `Layout`:

- **Header** (sticky, 3 rows): panel with the current speaker name colored by role, a truncated conversation id, and the magic-dust gauge.
- **Body** (scrolling): chat history with each line colored by speaker, a live streaming pane sandwiched between history and the input row (visible only while an agent is streaming), and a bottom-most input line with a blinking cursor.

### Colors (dark-theme palette)

| Who | Color   | Rationale |
|---|---------|---|
| `Morgana` | `#8b5cf6` | Base assistant identity — matches Cauldron's `--primary-color` |
| `Morgana (Agent)` | `#ec4899` | Specialised agent — matches Cauldron's `--secondary-color` |
| `You` | `white` | User input and committed messages |
| Streaming pane | `#ec4899` | Streamed chunks are assumed to originate from agents (base Morgana ships single messages), so the live reveal uses the secondary color without needing to thread `AgentName` through the chunk wire |
| Dust gauge | `#8b5cf6` / `#f59e0b` (≤30%) / `#ef4444` (≤10%) | Mirror of Cauldron's `.dust-meter` thresholds |

### Streaming and typewriter

The live streaming pane mirrors Cauldron's `StreamingService`:
- A single FIFO `Channel<InboundEvent>` carries both `MessageEvent`s (`/morgana-hook`) and `ChunkEvent`s (`/morgana-hook/chunk`). One drain loop = strictly FIFO — a trailing chunk can never be reordered after a final message and leak past the buffer clear.
- Two buffers: `streamingPending` (raw deltas waiting to be revealed) and `streamingDisplayed` (what's actually rendered).
- A `Timer` ticks every `Grimoire:StreamingResponse:TypewriterTickMilliseconds` (default 15 ms), pulling `Grimoire:StreamingResponse:TypewriterTickChars` (default 1) characters off `streamingPending` into `streamingDisplayed` and refreshing the live view.
- When the final `ChannelMessage` lands while the buffer is still draining, the commit to history is **deferred**: the message is stashed in `pendingFinalMessage` and `streamingComplete` is latched true. The next tick that observes an empty pending buffer commits the deferred final to `history`, resets the streaming state and stops the timer — so the user sees the typewriter complete naturally before history snaps. No "jump to full text" glitch.
- Timer cleanup is enforced by `try/finally` in `RunAsync.StartAsync`: the threadpool callback can never outlive the `LiveDisplayContext`.

### Input handling

- `Console.ReadKey(intercept: true)` on a background task, polling `Console.KeyAvailable` every 25 ms (Spectre.Console's Live rendering cannot share stdin with a first-class prompt).
- **Enter** — commits the current buffer: if it equals `/quit` the UI exits; otherwise it's appended to history as `You: …` and sent via `onSend`.
- **Backspace** — deletes the last character from the buffer.
- **Esc** — immediate exit.
- **Other printable chars** — appended to the buffer; layout refreshed on each keystroke.

### Resume

No resume in v1. Every Grimoire process start begins a fresh conversation. Keep this explicit: if future Grimoire picks up a conversation id from some store, the `ChannelMetadata.Build` handshake must also be re-announced (Morgana's `ConversationManagerActor` re-persists channel metadata on resume).

## Key Configuration (appsettings.json)

| Section | Purpose |
|---|---|
| `Grimoire:MorganaURL` | Morgana backend base URL for outbound REST (default `https://localhost:5001`) |
| `Grimoire:CallbackURL` | Absolute URL Morgana POSTs inbound messages to (default `https://localhost:5004/morgana-hook`) |
| `Grimoire:Authentication:SymmetricKey` | Shared HMAC key matching Morgana's `Issuers[].SymmetricKey` for `Name=grimoire` |
| `Grimoire:Authentication:Issuer` | Token issuer (default `grimoire`) |
| `Grimoire:Authentication:Audience` | Token audience (default `morgana.ai`) |
| `Grimoire:AgentExitMessage` | Template for the courtesy line appended when a specialised agent completes (default `"{0} has completed its spell. I'm back to you!"`; `{0}` is the agent's display name). Mirrors Cauldron's `Cauldron:AgentExitMessage`. |
| `Grimoire:LandingMessages` | String array of whimsical "warming up" lines printed to stdout during the startup window between `builder.Build()` and `ui.RunAsync`. Picked uniformly at random per process; overwritten by `AnsiConsole.Clear()` just before the Live UI takes over. Mirrors Cauldron's `Cauldron:LandingMessages` (same pool, same intent). |
| `Grimoire:StartupTimeoutSeconds` | How long (in seconds) to keep the landing line visible while waiting for Morgana's first webhook delivery before entering the Live UI anyway. Default `30`. Raise on slow LLM providers with cold starts (Ollama on CPU, Azure OpenAI in a distant region); lower for faster "something's wrong" feedback during development. Non-positive values fall back to the default. |
| `Grimoire:StreamingResponse:TypewriterTickMilliseconds` | Cadence between typewriter ticks (default `15`). Raise to slow the reveal (e.g. `30`–`40` for a more contemplative feel). Mirrors Cauldron's `Cauldron:StreamingResponse:TypewriterTickMilliseconds`. |
| `Grimoire:StreamingResponse:TypewriterTickChars` | Characters revealed per tick (default `1`). Raise above `1` to keep up with very fast LLM streams without lengthening the tick interval. Mirrors Cauldron's `Cauldron:StreamingResponse:TypewriterTickChars`. |

## Build and Run

- **Target**: .NET 10, console app hosted on Kestrel (via `Microsoft.NET.Sdk.Web`)
- **Build**: `dotnet build` from `Channels/Grimoire/` directory
- **Run**: `dotnet run` — default `https://localhost:5004` for the webhook listener (requires Morgana backend running and the `grimoire` issuer onboarded)
- **Docker**: `Channels/Grimoire/Grimoire.Dockerfile` (context is the repo root, mirroring the Morgana / Cauldron / Rune pattern). Grimoire is **not** launched by `docker compose up` — the service is profile-gated (`profiles: ["tui"]` in `docker-compose.yml`) so `up` skips it; the Spectre.Console Live UI needs to own the terminal, so it must be started interactively in a separate terminal after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases grimoire
  ```
  `compose run <service>` auto-activates the service's profiles so no `--profile tui` flag is needed. The `run --service-ports` invocation allocates the TTY Spectre requires *and* publishes `5004:5004` so Morgana's webhook callback can reach Grimoire's listener. `--use-aliases` is mandatory: unlike `compose up`, `compose run` does not register the service name as a network alias, so without it Morgana's callback to `http://grimoire:5004/morgana-hook` fails DNS resolution. The compose file sets `stdin_open: true` + `tty: true` on the `grimoire` service to keep this flow explicit. Rune and Grimoire share the `tui` profile and are mutually exclusive at runtime (only one process can own stdin/stdout).

## Conventions

- **Logging is silenced** at startup — the Spectre.Console Live UI owns the terminal; errors surface as red in-UI system lines.
- **Asymmetric trust** is a first-class design choice, not a bug — do not introduce webhook signing without revisiting the decision recorded in the `WebhookChannelService` notes.
- **Singletons** — one Grimoire process == one Grimoire session; if multi-session ever becomes a goal, move state (`ConsoleUiService.history`, `WebhookReceiverService.OnMessage`) onto a per-conversation scope first.
- **Server is source of truth** for final message text — even when streaming chunks have already painted the buffer, the deferred commit uses the final `ChannelMessage.Text` so any (rare) adapter rewrite wins over the streamed prefix.
