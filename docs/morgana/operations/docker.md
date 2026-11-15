---
title: Docker
permalink: /morgana/operations/docker/
eyebrow: Operating
lede: One compose file runs the backend and every companion on a dedicated network with a persistent volume for the conversations.
---

## What starts and what does not

| Service | Port | Started by `up` | Why |
|---|---|---|---|
| `morgana` | 5001 | ✅ | The backend |
| `gardening` | 5006 | ✅ | The example MCP server. It shares Morgana's network namespace so `localhost` works for the agent |
| `cauldron` | 5002 | ✅ | The browser channel |
| `grimoire` | 5004 | ❌ | A terminal UI that must own stdin and stdout |
| `rune` | 5003 | ❌ | The same. One terminal channel at a time |
| `alembic` | 5005 | ❌ | The authoring workbench. It talks to no Morgana and joins no network |

## Commands

```bash
# Production images from Docker Hub
cp production.env.template .env
docker compose up                                                # Morgana + Cauldron on :5002
docker compose run --rm --service-ports --use-aliases grimoire   # rich terminal
docker compose run --rm --service-ports --use-aliases rune       # poor terminal
docker compose --profile authoring up alembic                    # workbench on :5005

# Development: build from source
cp development.env.template .env
dotnet build ./Morgana && dotnet build ./Channels/Cauldron && dotnet build ./Channels/Grimoire && dotnet build ./Channels/Rune && dotnet build ./Alembic
docker compose --env-file .env --env-file .env.versions build
docker compose --env-file .env --env-file .env.versions up
```

> `--use-aliases` is mandatory for the terminal channels. `compose run` skips network aliases by default and without it Morgana's webhook callback fails to resolve the channel's name.
{: .warn}

## The `.env` file

| Variable | Purpose |
|---|---|
| `ENCRYPTIONKEY` | AES-256 key for the databases |
| `JWT_SYMMETRIC_KEY` | Signing key shared between each channel and its issuer entry |
| `LLM_{ECONOMY,EFFICIENCY,PERFORMANCE}_PROVIDER` | The provider of each tier |
| `LLM_{…}_APIKEY` · `_ENDPOINT` · `_MODEL` | Its credentials and model |
| `ACTORSYSTEM_TIER` | Tier of the framework actors |
| `MORGANA_VERSION` · `CAULDRON_VERSION` … | Image tags. `latest` by default |
| `MORGANA_PUBLIC_URL` · `PARTNER_0_*` | Federation. Commented out until you need it |

An unset variable falls back to its placeholder rather than injecting an empty string, so the startup check still catches it.

## The images

The backend image contains Morgana.Web and the five provider plugins. It also carries the **Examples** plugin under `plugins/`, so a fresh pull answers with the Greenhouse domain straight away. To run your own domain add your DLL to the same folder and the example can be removed.

Images are on Docker Hub: `mdesalvo/morgana`, `mdesalvo/cauldron`, `mdesalvo/grimoire`, `mdesalvo/rune` and `mdesalvo/alembic`.

## State

The `morgana-data` volume holds every conversation database at `/app/data`. Back it up together with the encryption key that opens it and keep the two apart.
