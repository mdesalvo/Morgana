---
title: Going to production
permalink: /production/
eyebrow: Installing me
lede: "What changes when I leave your laptop and start serving real customers: what runs where, what to back up and how to watch me work."
---

## What runs

My `docker-compose.yml` describes the whole household. Not everything starts with a plain `up`:

| Service | Address | Starts with `up` | Why |
|---|---|---|---|
| `morgana` | 5001 | ✅ | Me |
| `cauldron` | 5002 | ✅ | My browser mirror |
| `gardening` | 5006, inside my network only | ✅ | My shop's almanac server. Remove it with my shop |
| `grimoire` | 5004 | ❌ | A terminal mirror must own the terminal, so you start it by hand |
| `rune` | 5003 | ❌ | The same. One terminal mirror at a time |
| `alembic` | 5005 | ❌ | My still. It never talks to me, so it starts on its own: `docker compose --profile authoring up alembic` |

## Your tomes instead of my shop

My image carries my example shop in its `plugins/` folder, so that a fresh installation answers at once. For your own world, put your plugin in the same folder and take my shop out, together with the `gardening` service. Several plugins of yours may live side by side.

## What to back up

Two things, always kept **apart**:

- the `morgana-storage` volume, where I keep every conversation, sealed
- the encryption key that opens them

Whoever holds both can read every conversation. Whoever holds only the files can read nothing.

## Keys and addresses

- **One key per mirror.** The shipped setup shares one key among my three mirrors for convenience. In production give each its own, as described in [Your choices]({{ '/install/#my-mirrors' | relative_url }}).
- **Only the mirrors you use.** Remove from my list the mirrors you do not run.
- **My charm, only where you want it.** List the sites allowed to show Cauldron's widget in `Cauldron:Widget:AllowedEmbedOrigins`. An empty list admits nobody.
- **Behind a proxy.** If partners reach me through a proxy or a load balancer, tell me my public address in `AgentToAgent:PublicUrl`.

## Watching me work {#watching-it-run}

I describe my work in the open **OpenTelemetry** standard, so any monitoring tool that reads it can show you what I do: Jaeger, Grafana, Azure Monitor, Datadog and many more. Point me at yours:

```json
"OpenTelemetry": {
  "Enabled": true,
  "ServiceName": "Morgana",
  "Exporters": [
    { "Name": "console", "Enabled": false },
    { "Name": "otlp", "Enabled": true, "Endpoint": "http://your-collector:4317" }
  ]
}
```

What you will see:

| What | Tells you |
|---|---|
| One **trace per turn** | Each step of the journey from [Actors]({{ '/actors/' | relative_url }}) as its own span: guard, diviner, guide, witch, any sister she asked. Where time goes, at a glance |
| Which **subject** and which **witch** | What your customers really ask about and how sure my diviner was |
| Which **spells** were cast | Their names only, never what was passed to them |
| `morgana.dust.consumed` | The dust spent, by who spent it. Beside it the vendor's token counts, `gen_ai.usage.*` |
| `morgana.security.events` | What happened at each of my doors: admitted, refused, or refused because nobody could check |

The text of messages never goes into traces unless you set `EnableSensitiveData`. Leave it off wherever conversations hold personal data.

## One last look

Before you open the doors, run my [checklist]({{ '/install/#before-i-meet-your-customers' | relative_url }}).
