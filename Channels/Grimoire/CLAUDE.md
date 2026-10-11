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

## What lives here

The terminal UI, the host wiring and every behaviour the two TTY channels share (keys, command
palette, reply and command deadlines, startup checks, webhook endpoints) live in **`Morgana.Terminal`**:
`TerminalUiService` and `TerminalChannelExtensions` document them. The streaming typewriter
(`StreamingPaneService`) and the quick-reply prompt (`QuickReplyPromptService`) are library widgets too,
switched on by Grimoire's profile flags. Grimoire is:

- `Messages/GrimoireChannelProfile.cs`: who Grimoire is and the full capability profile it declares
- `Messages/GrimoireTerminalTheme.cs`: Cauldron's palette (`#8b5cf6` base Morgana, `#ec4899` a
  specialised agent) and a block-glyph progress bar
- `Services/MarkdownMessageRenderService.cs`: Grimoire's `IMessageRenderer`, composing
  `MarkdownTerminalRenderService` (Markdig to Spectre rows) and `RichCardTerminalRenderService` (the
  card schema as a bordered box under the prose)
- `Program.cs`: the profile, the theme and the renderer handed to the library

Wire types, `StreamChunkRequest` included, come from `Morgana.Contracts`, shared with Morgana.Web's
`WebhookChannelService`: there is no private copy on either side.

## Channel handshake

```csharp
Coordinates  = { ChannelName = "grimoire", DeliveryMode = "webhook", CallbackUrl = "<Grimoire:CallbackURL>" }
Capabilities = { SupportsRichCards: true, SupportsQuickReplies: true, SupportsStreaming: true,
                 SupportsMarkdown: true, MaxMessageLength: null }
```

Morgana requires `callbackUrl` to be an **absolute** URI when `deliveryMode=webhook`.

## Authentication

JWTs are HMAC-SHA256 with `Grimoire:Authentication:SymmetricKey`, issuer `grimoire`, audience
`morgana.ai`, five minutes. **Trust is asymmetric by design**: Grimoire signs its calls toward Morgana,
Morgana does not sign the webhook POST toward Grimoire (`WebhookChannelService`'s convention). Do not add
webhook signing without revisiting the decision recorded there.

**Onboarding a fresh Morgana instance:**
1. Add `{ "Name": "grimoire", "SymmetricKey": "<≥256 bit, base64>" }` to
   `Morgana:Authentication:Issuers[]`. That list holds **channels and nothing else**: the key buys
   the conversation API and nothing published under `/a2a`
2. Put the same key under `Grimoire:Authentication:SymmetricKey` through user-secrets or an
   environment variable, never a commit
3. Start Morgana (`:5001`), then `dotnet run` from `Channels/Grimoire/` (`:5004`)

**There is no resume**: every process start begins a fresh conversation.

## Key configuration

| Section | Purpose |
|---|---|
| `Grimoire:MorganaURL` · `:CallbackURL` | Backend base URL; the absolute URL Morgana POSTs to (default `https://localhost:5004/morgana-hook`) |
| `Grimoire:Authentication:*` | `SymmetricKey` matching Morgana's entry for `Name=grimoire`, plus `Issuer` and `Audience` |
| `Grimoire:MaxInputLength` | What the user may type in one turn (500) |
| `Grimoire:ReplyTimeoutSeconds` | How long a turn may go silent before the prompt comes back with a red notice (120). It measures silence, not duration: every chunk restarts the count and a typewriter still revealing text holds it off |
| `Grimoire:CommandTimeoutSeconds` | How long the prompt waits on one of Morgana's commands before calling it off (default 120) |
| `Grimoire:StreamingResponse:*` | `TypewriterTickMilliseconds` (15), `TypewriterTickChars` (1) |
| `Grimoire:LandingMessages` | The startup lines, cleared when the Live UI takes over. Mirrors Cauldron's |
| `Grimoire:StartupTimeoutSeconds` | How long to wait for Morgana's first delivery before entering the Live UI anyway (default 30). Raise it on providers with cold starts |

## Build and Run

- **Target**: .NET 10 console app hosted on Kestrel (`Microsoft.NET.Sdk.Web`)
- **Run**: `dotnet run` from `Channels/Grimoire/` — listener on `https://localhost:5004`
- **Docker**: profile-gated (`tui`), so `compose up` skips it. The Live UI must own the terminal, so
  start it interactively after Morgana is up:
  ```bash
  docker compose --env-file .env --env-file .env.versions run --rm --service-ports --use-aliases grimoire
  ```
  `--service-ports` allocates the TTY **and** publishes 5004 so the callback can land. **`--use-aliases`
  is mandatory**: unlike `up`, `run` registers no network alias, so without it Morgana's callback to
  `http://grimoire:5004/morgana-hook` fails DNS resolution.

## Conventions

- **Nothing from the callback is drawn raw**: the webhook is unauthenticated by design, so prose,
  speaker names, quick-reply labels and card fields all pass `TerminalCellService.StripControlCharacters`
  before reaching Spectre. `Markup.Escape` guards the `[` of the markup syntax and nothing else: an ESC or
  OSC sequence slips straight through it and is obeyed by the user's terminal. A new field drawn by the
  renderers follows the same rule
- **The server is the source of truth** for final text: the streamed prefix is replaced by the final
  `ChannelMessage.Text`, so a rare adapter rewrite wins over what was revealed
