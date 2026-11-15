---
title: Startup validation
permalink: /morgana/operations/startup-validation/
eyebrow: Operating
lede: A plugin declares its world in three places at once and nothing in the type system ties them together. Morgana does it at boot with nine checks, every one of them fatal.
---

## Why it fails fast

Attributes in C#, intents and prose in `agents.json` and credentials and policies in configuration can each be correct on their own and wrong together. Left alone, that mismatch reaches a user as a conversation that never answers, on a host that already passed its health probe. Morgana resolves its configuration and registry services immediately after building the container, so every fault below is a **startup refusal**.

## The nine checks

| # | Check | Prevents |
|---|---|---|
| 1 | Every configured intent has an agent and every agent an intent | A classified intent routed nowhere or a desk nobody can reach |
| 2 | Every agent declares `[RequiresLLMTier]` | An agent needing deep reasoning silently served by a cheap model |
| 3 | Every `[ConsultsAgent]` names a reachable colleague and no two fold to one function name | A consultation that fails at the first question |
| 4 | Tools: orphans warn and duplicates for one intent are refused. Every tool and parameter has a `[Description]`. A context parameter is a `string`. No domain tool takes a base tool's name | Two implementations claiming one tool and a tool the model cannot understand |
| 5 | Plugin `agents.json` files merge with no duplicated intent or prompt id and none declares `other` or a framework prompt id | One plugin silently overwriting another |
| 6 | All three tiers declared, each naming a registered provider with a connection and no field on its placeholder. Each model priced. `ActorSystem:Tier` names a tier | A deployment shipped with the template's secrets still in place |
| 7 | Every admitted issuer carries a name and a key of at least 256 bits. No name admitted twice | A door held shut by a key too short to hold it |
| 8 | `ValidateTrustConfiguration`: each partner names somebody once, does something and carries a key that can sign | A relationship that looks configured and admits nobody |
| 9 | `ValidatePublishedAddress`: `PublicUrl` is absolute and on a bearer-carrying scheme and names one interface | A card sending peers to the wrong address or carrying tokens in clear |

## Reading a refusal

Each refusal names what is wrong and where. The usual first-day ones are:

- **A placeholder left in place.** `_SECURE_OVERRIDE_` or `_FUNCTIONAL_OVERRIDE_` survived in a tier, an issuer key or the encryption key
- **An intent without an agent.** You added it to `agents.json` and forgot the class
- **A missing `[Description]`.** Every tool and every parameter needs one

Alembic runs the deterministic subset of these checks while you are still writing, when a fix is free. See [Alembic]({{ '/alembic/' | relative_url }}).
