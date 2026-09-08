# Grimoire — Morgana's rich-TTY webhook channel

## What is Grimoire

A **.NET 10 console application**, the rich-TTY reference channel: it declares the **same full
capability profile as Cauldron** — every expressive feature on, no length cap — then renders that
undegraded output inside a Spectre.Console terminal. It is the **textual Cauldron**.

It closes the channel × capability matrix, which is the whole reason it exists:

|          | HTML     | TTY          |
|----------|----------|--------------|
| **Full** | Cauldron | **Grimoire** |
| **Poor** | —        | Rune         |

A pure-text channel does not have to be a poor one: the same card schema, the same chunks, the same
quick replies land here intact and become Spectre primitives. Because the profile is full,
`MorganaChannelAdapter` short-circuits on every turn — **Grimoire never exercises the degradation
path**, which stays Rune's job.

**Not a Cauldron fork**: its own channel identity, `iss=grimoire`, its own key, its own port (5004).
Grimoire and Rune share the `tui` docker profile, mutually exclusive at runtime — only one process
can own stdin and stdout.

## Channel handshake

```csharp
Coordinates  = { ChannelName = "grimoire", DeliveryMode = "webhook", CallbackUrl = "<Grimoire:CallbackURL>" }
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true, SupportsStreaming: true,
                 SupportsMarkdown: true, MaxMessageLength: null }
```

Morgana's gate additionally requires `callbackUrl` to be an **absolute** URI when
`deliveryMode=webhook`, fail-closed at the handshake.

## Authentication

`MorganaAuthHandler` mints short-lived JWTs: HMAC-SHA256 with `Grimoire:Authentication:SymmetricKey`,
issuer `grimoire`, audience `morgana.ai`, five minutes.

**Trust is asymmetric by design**: Grimoire signs its outbound calls toward Morgana; Morgana does
**not** sign the inbound webhook POST toward Grimoire. That matches `WebhookChannelService`'s
convention (the GitHub / Stripe / Twilio style) — it is not a gap, so do not add webhook signing
without revisiting the decision recorded there.

**Onboarding a fresh Morgana instance:**
1. Add `{ "Name": "grimoire", "SymmetricKey": "<≥256 bit, base64>" }` to
   `Morgana:Authentication:Issuers[]`. That list holds **channels and nothing else**: the key buys
   the conversation API and nothing published under `/a2a`
2. Put the same key under `Grimoire:Authentication:SymmetricKey` through user-secrets or an
   environment variable, never a commit
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Grimoire/` (`:5004`)

## Wire contracts

A `ProjectReference` on **`Morgana.Contracts`**, consumed directly — requests, responses and
`StreamChunkRequest`, the `{callbackUrl}/chunk` body, which lives there too and is consumed by
Morgana.Web's own `WebhookChannelService`. There is no private copy on either side. Channel identity
lives channel-side in `Messages/GrimoireChannelMetadata.cs`.

## Terminal UI

Spectre.Console `LiveDisplay` plus `Layout`: a sticky header (speaker, truncated conversation id, the
magic-dust gauge) over a scrolling body, with the live streaming pane sandwiched between history and
the input line.

Colours mirror Cauldron's palette: `#8b5cf6` for base Morgana, `#ec4899` for a specialised agent,
white for the user, the dust gauge crossing amber at 30% and red at 10%. The streaming pane uses the
agent colour because streamed chunks are assumed to come from agents, which avoids threading
`AgentName` through the chunk wire.

### Streaming the deferred commit

- **One FIFO `Channel<InboundEvent>` carries both messages and chunks.** A single drain loop is what
  guarantees a trailing chunk can never be reordered after a final message, leaking past the buffer
  clear.
- Two buffers: what is waiting to be revealed, what is actually rendered. A timer moves
  `TypewriterTickChars` from one to the other every `TypewriterTickMilliseconds`.
- **When the final message lands while the buffer is still draining, the commit to history waits**:
  the message is stashed, the completion is latched, then the first tick observing an empty buffer
  commits it. The user sees the typewriter finish naturally instead of the text snapping to full.
- Timer cleanup is enforced in a `finally`, so the threadpool callback can never outlive the live
  display context.

### Input

`Console.ReadKey(intercept: true)` on a background task polling every 25 ms — Spectre's Live
rendering cannot share stdin with a first-class prompt. **Enter** commits (or exits on `/quit`),
**Backspace** deletes, **Esc** exits.

### Resume

**There is none.** Every process start begins a fresh conversation. Keep that explicit: a future
Grimoire picking up a conversation id from a store must **re-announce the handshake**, since
`ConversationManagerActor` re-persists channel metadata on resume.

## Key configuration

| Section | Purpose |
|---|---|
| `Grimoire:MorganaURL` · `:CallbackURL` | Backend base URL; the absolute URL Morgana POSTs to (default `https://localhost:5004/morgana-hook`) |
| `Grimoire:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=grimoire`, plus `Issuer` and `Audience` |
| `Grimoire:AgentExitMessage` · `:LandingMessages` | The courtesy line on agent completion; the startup lines, cleared when the Live UI takes over. Both mirror Cauldron's |
| `Grimoire:StartupTimeoutSeconds` | How long to wait for Morgana's first delivery before entering the Live UI anyway (default 30). Raise it on providers with cold starts |
| `Grimoire:StreamingResponse:*` | `TypewriterTickMilliseconds` (15), `TypewriterTickChars` (1) |

## Build and Run

- **Target**: .NET 10 console app hosted on Kestrel (`Microsoft.NET.Sdk.Web`)
- **Run**: `dotnet run` from `Channels/Grimoire/` — listener on `https://localhost:5004`
- **Docker**: profile-gated (`tui`), so `compose up` skips it. The Live UI must own the terminal, so
  start it interactively after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases grimoire
  ```
  `run` auto-activates the service's profiles, `--service-ports` allocates the TTY **and** publishes
  5004 so the callback can land. **`--use-aliases` is mandatory**: unlike `up`, `run` registers
  no network alias, so without it Morgana's callback to `http://grimoire:5004/morgana-hook` fails DNS
  resolution.

## Conventions

- **Logging is silenced at startup** — the Live UI owns the terminal; errors surface as red in-UI lines
- **Singletons**: one process is one session. Multi-session would first have to move
  `ConsoleUiService.history` and the receiver's callback onto a per-conversation scope
- **The server is the source of truth** for final text: the deferred commit uses the final
  `ChannelMessage.Text`, so a rare adapter rewrite wins over the streamed prefix
