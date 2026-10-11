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

## What lives here

Almost nothing, by design. The terminal UI, the host wiring and every behaviour the two TTY channels
share (keys, command palette, reply and command deadlines, startup checks, webhook endpoint) live in
**`Morgana.Terminal`**: `TerminalUiService` and `TerminalChannelExtensions` document them. Rune is:

- `Messages/RuneChannelProfile.cs`: who Rune is and what little it can render. The library reads the
  handshake, the token claims and the `Rune:` configuration root from it and switches the streaming
  pane and the quick replies on only where its flags say so
- `Messages/RuneTerminalTheme.cs`: its emeralds (`#10b981` base Morgana, `#6ee7b7` a specialised
  agent) and a progress bar in plain characters
- `Program.cs`: the profile and the theme handed to the library, nothing else

It draws through the library's `PlainTextMessageRenderService`. `QuickReply` and `RichCard` arrive in the
shared `Morgana.Contracts` types **already stripped by the adapter upstream**, which is the point of the channel.

## Channel handshake

```csharp
Coordinates  = { ChannelName = "rune", DeliveryMode = "webhook", CallbackUrl = "<Rune:CallbackURL>" }
Capabilities = { SupportsRichCards: false, SupportsQuickReplies: false, SupportsStreaming: false,
                 SupportsMarkdown: false, MaxMessageLength: <Rune:MaxMessageLength, default 500> }
```

Morgana requires `callbackUrl` to be an **absolute** URI when `deliveryMode=webhook`. A
`MaxMessageLength` below `Morgana:AdaptiveMessaging:RichFeaturesMinLength` **also forces rich cards and
quick replies off server-side**, even were a future Rune to claim them — which is why the aggressive
default stays.

## Authentication

JWTs are HMAC-SHA256 with `Rune:Authentication:SymmetricKey`, issuer `rune`, audience `morgana.ai`, five
minutes. **Trust is asymmetric by design**: Rune signs its calls toward Morgana, Morgana does not sign
the webhook POST toward Rune (`WebhookChannelService`'s convention). Do not add webhook signing without
revisiting the decision recorded there.

**Onboarding a fresh Morgana instance:**
1. Add `{ "Name": "rune", "SymmetricKey": "<≥256 bit, base64>" }` to
   `Morgana:Authentication:Issuers[]`. That list holds **channels and nothing else**: the key buys
   the conversation API and nothing published under `/a2a`
2. Put the same key under `Rune:Authentication:SymmetricKey` through user-secrets or an environment
   variable, never a commit
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Rune/` (`:5003`)

**There is no resume**: every process start begins a fresh conversation.

## Key configuration

| Section | Purpose |
|---|---|
| `Rune:MorganaURL` · `:CallbackURL` | Backend base URL; the absolute URL Morgana POSTs to (default `https://localhost:5003/morgana-hook`) |
| `Rune:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=rune`, plus `Issuer` and `Audience` |
| `Rune:MaxMessageLength` | The cap advertised at the handshake. Default `500`, aggressive on purpose so the downgrade runs every turn. Raising it (say `2000`) softens the rewrite without losing the profile; anything below `RichFeaturesMinLength` keeps rich features forced off server-side |
| `Rune:MaxInputLength` | What the *user* may type in one turn, default `500`. Independent of `MaxMessageLength`, which caps what Morgana may send back: the two travel in opposite directions and nothing couples them |
| `Rune:ReplyTimeoutSeconds` | How long a sent turn may go unanswered before the prompt comes back with a red notice (180). Higher than Grimoire's: a poor channel sees nothing until the reply lands, so the wait covers the whole turn — tool chains and remote colleagues included — not the silence between chunks |
| `Rune:CommandTimeoutSeconds` | How long the prompt waits on one of Morgana's commands before calling it off (default 120) |
| `Rune:LandingMessages` | The startup lines, cleared when the Live UI takes over. Mirrors Cauldron's |
| `Rune:StartupTimeoutSeconds` | How long to wait for Morgana's first delivery before entering the Live UI anyway (default 30). Raise it on providers with cold starts |

## Build and Run

- **Target**: .NET 10 console app hosted on Kestrel (`Microsoft.NET.Sdk.Web`)
- **Run**: `dotnet run` from `Channels/Rune/` — listener on `https://localhost:5003`
- **Docker**: profile-gated (`tui`), so `compose up` skips it. The Live UI must own the terminal, so
  start it interactively after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases rune
  ```
  `--service-ports` allocates the TTY **and** publishes 5003 so the callback can land. **`--use-aliases`
  is mandatory**: unlike `up`, `run` registers no network alias, so without it Morgana's callback to
  `http://rune:5003/morgana-hook` fails DNS resolution.

## Conventions

- **Never enrich Rune.** Its value is exactly what it cannot do: raise a capability here and the
  degradation path stops being exercised anywhere. Sharing `Morgana.Terminal` with Grimoire does not
  soften this: a feature reaches Rune only through the flags of `RuneChannelProfile`, which is where
  that would have to be written. Local chrome such as the command palette is not a capability
