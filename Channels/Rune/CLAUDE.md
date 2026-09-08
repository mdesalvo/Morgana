# Rune — Morgana's poor-but-honest webhook channel

## What is Rune

A **.NET 10 console application**, the channel that exists to be **poor on purpose**: a 500-character
hard cap, no rich cards, no quick replies, no streaming, no markdown. Its job is to exercise
Morgana's capability-degradation path **on every turn** — the channel-adapter rewrite, the upstream
streaming suppression, the webhook delivery mode — so that code cannot silently rot.

A second rich channel would be a fast-path twin of Cauldron, validating nothing already exercised. It
is also **not a rogue echo client**: it self-issues JWTs under `iss=rune` with its own key, so the
per-issuer gate is closed end to end by a second channel identity.

It holds the poor-TTY cell of the matrix; Grimoire holds rich-TTY, Cauldron rich-Web. Rune and
Grimoire share the `tui` docker profile, mutually exclusive at runtime — only one process can own
stdin and stdout.

## Channel handshake

```csharp
Coordinates  = { ChannelName = "rune", DeliveryMode = "webhook", CallbackUrl = "<Rune:CallbackURL>" }
Capabilities = { SupportsRichCards: false, SupportsQuickReplies: false, SupportsStreaming: false,
                 SupportsMarkdown: false, MaxMessageLength: <Rune:MaxMessageLength, default 500> }
```

Morgana's gate additionally requires `callbackUrl` to be an **absolute** URI when
`deliveryMode=webhook`, fail-closed at the handshake. A `MaxMessageLength` below
`Morgana:AdaptiveMessaging:RichFeaturesMinLength` **also forces rich cards and quick replies off
server-side**, even were a future Rune to claim them — which is why the aggressive default stays.

## Lifecycle

Logging is cleared, Kestrel starts listening, the webhook receiver is wired to the UI queue, the
conversation is opened with the handshake, then `RunAsync` blocks on the Live loop until `/quit` or
`Esc`. A `finally` ends the conversation and stops the host.

## Authentication

`MorganaAuthHandler` mints short-lived JWTs: HMAC-SHA256 with `Rune:Authentication:SymmetricKey`,
issuer `rune`, audience `morgana.ai`, five minutes.

**Trust is asymmetric by design**: Rune signs its outbound calls toward Morgana; Morgana does **not**
sign the inbound webhook POST toward Rune. That matches `WebhookChannelService`'s convention (the
GitHub / Stripe / Twilio style) — it is not a gap, so do not add webhook signing without revisiting
the decision recorded there.

**Onboarding a fresh Morgana instance:**
1. Add `{ "Name": "rune", "SymmetricKey": "<≥256 bit, base64>" }` to
   `Morgana:Authentication:Issuers[]`. That list holds **channels and nothing else**: the key buys
   the conversation API and nothing published under `/a2a`
2. Put the same key under `Rune:Authentication:SymmetricKey` through user-secrets or an environment
   variable, never a commit
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Rune/` (`:5003`)

## Wire contracts

A `ProjectReference` on **`Morgana.Contracts`**, consumed directly, responses included. Rune still
renders no `QuickReply` or `RichCard`: they arrive as part of the shared contract **already stripped
by the adapter upstream**, which is the point of the channel. Channel identity lives channel-side in
`Messages/RuneChannelMetadata.cs`.

## Terminal UI

Spectre.Console `LiveDisplay` plus `Layout`: a sticky header (speaker, truncated conversation id)
over a scrolling body, with the input line at the bottom. There is no streaming pane — there is no
streaming. Colours: `magenta1` for base Morgana, `hotpink` for a specialised agent, white for the
user.

### Input

`Console.ReadKey(intercept: true)` on a background task polling every 25 ms — Spectre's Live
rendering cannot share stdin with a first-class prompt. **Enter** commits (or exits on `/quit`),
**Backspace** deletes, **Esc** exits.

### Resume

**There is none.** Every process start begins a fresh conversation. Keep that explicit: a future Rune
picking up a conversation id from a store must **re-announce the handshake**, since
`ConversationManagerActor` re-persists channel metadata on resume.

## Key configuration

| Section | Purpose |
|---|---|
| `Rune:MorganaURL` · `:CallbackURL` | Backend base URL; the absolute URL Morgana POSTs to (default `https://localhost:5003/morgana-hook`) |
| `Rune:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=rune`, plus `Issuer` and `Audience` |
| `Rune:MaxMessageLength` | The cap advertised at the handshake. Default `500`, aggressive on purpose so the downgrade runs every turn. Raising it (say `2000`) softens the rewrite without losing the profile; anything below `RichFeaturesMinLength` keeps rich features forced off server-side |
| `Rune:AgentExitMessage` · `:LandingMessages` | The courtesy line on agent completion; the startup lines, cleared when the Live UI takes over. Both mirror Cauldron's |
| `Rune:StartupTimeoutSeconds` | How long to wait for Morgana's first delivery before entering the Live UI anyway (default 30). Raise it on providers with cold starts |

## Build and Run

- **Target**: .NET 10 console app hosted on Kestrel (`Microsoft.NET.Sdk.Web`)
- **Run**: `dotnet run` from `Channels/Rune/` — listener on `https://localhost:5003`
- **Docker**: profile-gated (`tui`), so `compose up` skips it. The Live UI must own the terminal, so
  start it interactively after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases rune
  ```
  `run` auto-activates the service's profiles, `--service-ports` allocates the TTY **and** publishes
  5003 so the callback can land. **`--use-aliases` is mandatory**: unlike `up`, `run` registers
  no network alias, so without it Morgana's callback to `http://rune:5003/morgana-hook` fails DNS
  resolution.

## Conventions

- **Logging is silenced at startup** — the Live UI owns the terminal; errors surface as red in-UI lines
- **Singletons**: one process is one session. Multi-session would first have to move the UI history
  and the receiver's callback onto a per-conversation scope
- **Never enrich Rune.** Its value is exactly what it cannot do: raise a capability here and the
  degradation path stops being exercised anywhere
