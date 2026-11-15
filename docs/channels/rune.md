---
title: Rune
permalink: /channels/rune/
eyebrow: Channel · poor terminal
lede: Poor on purpose. Rune can show 500 characters of plain text and nothing else, which is exactly why it exists.
---

<div class="pills"><span class="pill">Spectre.Console</span><span class="pill">Webhook delivery</span><span class="pill">Port 5003</span><span class="pill you">Cards · replies · streaming · markdown off</span></div>

## A tour

<div class="shots">
{% include shot.html src="rune/welcome.jpg" caption="Welcome in plain text: no buttons, so the intents are named in the sentence" wide=true %}
{% include shot.html src="rune/turn.jpg" caption="An agent's answer rewritten by the channel adapter to fit 500 characters" wide=true %}
{% include shot.html src="rune/help.jpg" caption="The command palette works here as in Grimoire" wide=true %}
</div>

## Why a poor channel

A second rich channel would be a fast-path twin of Cauldron and would validate nothing that is not already exercised. Rune does the opposite. Its profile turns every rich feature off:

```csharp
Capabilities = new ChannelCapabilities(
    SupportsRichCards:    false,
    SupportsQuickReplies: false,
    SupportsStreaming:    false,
    SupportsMarkdown:     false,
    MaxMessageLength:     500);
```

So on **every turn** Morgana must rewrite the answer, drop what cannot be drawn and fit the budget. That exercises the channel adapter's rewrite, the suppression of streaming upstream and the webhook mode, so that code cannot quietly rot.

It is also not a rogue echo client. Rune signs its own tokens under `iss=rune` with its own key, which closes the per-issuer gate end to end with a second channel identity.

## Talking to Rune

There are no buttons, so the user writes whole sentences. This is a feature of the test as much as of the channel: *"I would like to see my invoices, my customer code is P994E"* works and a bare code or a button label does not.

## Commands

The palette is the same as Grimoire's: type `/` then Tab or Enter. `/compact` summarises the agent's context and leaves your transcript unchanged. `/export` writes the conversation to a file. `/new` starts a fresh conversation. `/status` shows the connection and the state of the conversation. `/help` explains the channel and `/exit` leaves.

## Run it

```bash
docker compose run --rm --service-ports --use-aliases rune
cd Channels/Rune && dotnet run          # from source
```

`Rune:MaxMessageLength` sets the budget it declares. Keys are on the [reference page]({{ '/morgana/configuration/reference/' | relative_url }}).
