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

Backend URL, callback URL and signing key are checked before anything else, every problem reported
at once: the two URLs must be absolute `http(s)`, the key must no longer be the shipped
`_SECURE_OVERRIDE_` marker. Each is fatal and the Live UI would swallow the reason. Logging is then cleared,
Kestrel starts listening and `ConversationLifecycleService` wires the webhook
receiver to the UI queue, opens the conversation with the handshake (retried at
`MorganaStartRetryPolicy`'s pace while Morgana is unreachable) and blocks on the Live loop until
`/quit` or `Esc`. A `finally` ends the conversation and stops the host. The webhook accepts only
deliveries for the conversation on screen and answers 404 to any other.

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

Spectre.Console `LiveDisplay` plus `Layout`: a sticky header (speaker, truncated conversation id,
dust gauge, scroll and input-length indicators) over a scrolling body, with the input line at the
bottom. There is no streaming pane — there is no streaming. Colours: `#10b981` emerald for base
Morgana, `#6ee7b7` light green for a specialised agent, white for the user, orange for advisory
warnings and red for errors.

### Input

`Console.ReadKey(intercept: true)` on a background task polling every 25 ms — Spectre's Live
rendering cannot share stdin with a first-class prompt. **Enter** commits (or exits on `/quit`),
**Backspace** and **Delete** remove around the caret, **←/→** move it, **Esc** exits. At rest
**↑/↓** and **PgUp/PgDn** scroll the transcript back; they are ignored while a turn is in flight, so
the window never moves under an arriving reply. Repainting waits for the keystrokes to stop, so a
pasted line costs one frame rather than one per character.

A turn that Morgana accepts but never answers releases the prompt after `Rune:ReplyTimeoutSeconds`
with a red notice, instead of locking the conversation until the process is killed.

A reply is never lost to a callback that was briefly unreachable: a webhook is the transport where
Morgana itself sees the delivery fail, so Morgana delivers the message again for about half a
minute (`WebhookChannelService`), already degraded for Rune. The deadline is left for the turn
Morgana never answered at all.

### Resume

**There is none.** Every process start begins a fresh conversation. A future Rune picking up a
conversation id from a store would announce nothing again: the handshake is settled on Morgana's
record at start and read back from there, by a resume and by a Morgana that restarted meanwhile alike.

## Key configuration

| Section | Purpose |
|---|---|
| `Rune:MorganaURL` · `:CallbackURL` | Backend base URL; the absolute URL Morgana POSTs to (default `https://localhost:5003/morgana-hook`) |
| `Rune:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=rune`, plus `Issuer` and `Audience` |
| `Rune:MaxMessageLength` | The cap advertised at the handshake. Default `500`, aggressive on purpose so the downgrade runs every turn. Raising it (say `2000`) softens the rewrite without losing the profile; anything below `RichFeaturesMinLength` keeps rich features forced off server-side |
| `Rune:MaxInputLength` | What the *user* may type in one turn, default `500`. Independent of `MaxMessageLength`, which caps what Morgana may send back: the two travel in opposite directions and nothing couples them |
| `Rune:ReplyTimeoutSeconds` | How long a sent turn may go unanswered before the prompt comes back with a red notice (default 180). Deliberately higher than Grimoire's: a poor channel sees nothing until the reply lands, so the wait covers the whole turn — tool chains and remote colleagues included — not the silence between chunks |
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
