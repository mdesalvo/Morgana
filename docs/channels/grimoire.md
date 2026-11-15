---
title: Grimoire
permalink: /channels/grimoire/
eyebrow: Channel · rich terminal
lede: The textual Cauldron. The same turns with the same cards and the same buttons, drawn inside a terminal.
---

<div class="pills"><span class="pill">Spectre.Console</span><span class="pill">Webhook delivery</span><span class="pill">Port 5004</span><span class="pill ok">All capabilities on</span></div>

## A tour

<div class="shots">
{% include shot.html src="grimoire/welcome.jpg" caption="Welcome: the presentation and the buttons as an arrow-key selector" wide=true %}
{% include shot.html src="grimoire/palette.jpg" caption="Typing / opens the command palette" wide=true %}
{% include shot.html src="grimoire/landing.jpg" caption="An agent takes over. The header names it and shows the conversation and the dust level" wide=true %}
{% include shot.html src="grimoire/invoices.jpg" caption="A rich card becomes a bordered box under the prose" wide=true %}
</div>

## What it proves

A pure-text channel does not have to be a poor one. Grimoire declares the **same full capability profile as Cauldron** and renders it undegraded. Content arrives intact and is made terminal-native locally:

- **Markdown** through a Markdig to Spectre renderer
- **Rich cards** as bordered boxes
- **Quick replies** as an inline selector with arrows and Enter
- **Streaming** in a live pane with a typewriter cadence

Because it uses the same webhook delivery as Rune, it also shows that Morgana adapts to the **capability profile** and not to the transport.

## Using it

| Key | Does |
|---|---|
| ↑ ↓ and Enter | Choose among quick replies |
| Typing | Write to Morgana whenever no quick replies are on screen |
| `/` then Tab or Enter | Open the command palette and run a command |
| Esc | Close an open panel. On the bare prompt it leaves Grimoire |

| Command | Does |
|---|---|
| `/compact` | Summarises the active agent's context. The transcript on screen is unchanged |
| `/export` | Writes the conversation to a file |
| `/new` | Starts a fresh conversation with Morgana |
| `/status` | Shows the connection and the state of the conversation |
| `/help` | Explains the channel and lists the commands |
| `/exit` | Leaves |

While quick replies are shown they own the prompt: arrows and Enter pick one and every other key is swallowed, except `/`. The header counts typed characters against `MaxInputLength` and stops at the cap.

## Run it

```bash
docker compose run --rm --service-ports --use-aliases grimoire
cd Channels/Grimoire && dotnet run      # from source
```

Grimoire and Rune share a profile and are mutually exclusive at runtime: only one process can own your terminal. Keys and ports are on the [reference page]({{ '/morgana/configuration/reference/' | relative_url }}).
