# A2A extension: bearer issuance, v1

    https://mdesalvo.github.io/Morgana/a2a/extensions/bearer-issuance/v1/

An [A2A](https://a2a-protocol.org/) agent card already carries the two halves of a transport
contract: **where** an agent answers (`supportedInterfaces`) and **which** authentication scheme it
requires (`securitySchemes` / `securityRequirements`). For an HTTP bearer scheme, however, the
standard stops at the word *bearer*: it says a token is required, never how a caller is supposed to
produce one. A caller holding a shared secret still has to be told, out of band, which `iss` to sign
under and which `aud` to name — and out of band means configured on the calling side, which is
precisely the coupling a discovery document exists to remove.

This extension closes that gap and nothing else. It declares the two claim values the publishing
agent will validate, so a caller that already holds the shared secret needs no configuration beyond
that secret.

## Declaration

The extension is declared in the agent card's `capabilities.extensions`:

```jsonc
{
  "capabilities": {
    "extensions": [
      {
        "uri": "https://mdesalvo.github.io/Morgana/a2a/extensions/bearer-issuance/v1",
        "description": "Issuer and audience a caller must mint its bearer token under.",
        "required": false,
        "params": {
          "issuer": "morgana",
          "audience": "morgana.ai"
        }
      }
    ]
  },
  "securitySchemes": {
    "morgana-bearer": {
      "type": "http",
      "scheme": "bearer",
      "bearerFormat": "JWT"
    }
  },
  "securityRequirements": [
    { "morgana-bearer": [] }
  ]
}
```

### `params`

| Field      | Type   | Required | Meaning                                                              |
|------------|--------|----------|----------------------------------------------------------------------|
| `issuer`   | string | yes      | Value the publisher expects in the token's `iss` claim.              |
| `audience` | string | yes      | Value the publisher expects in the token's `aud` claim.              |

Both are opaque identifiers, compared for equality. Neither is required to be a URL, a hostname, or
a resource anybody owns.

## `required` is false, deliberately

The requirement to authenticate is already stated in standard form by `securityRequirements`, which
every A2A consumer reads. This extension only says how to *mint* a token, which is a convenience for
a caller that can mint one — not a condition for talking to the agent. A consumer that already holds
a token issued out of band, or one that ignores the extension entirely, is unaffected: it is held to
the standard requirement and nothing more.

Consequently, an implementation that does not understand this extension MUST NOT refuse the card.

## What it does not carry

- **The secret.** The card is served unauthenticated — discovery is what tells a caller how to
  authenticate, so it cannot itself require authentication — and a public document is no place for a
  key. The shared secret is exchanged out of band, once, and is the only thing a caller configures.
- **The algorithm.** `bearerFormat: "JWT"` on the security scheme says the token is a JWT; which
  signature algorithm the publisher accepts is a property of the credential it handed out, not of
  its public description.
- **Authorization.** The extension says how a caller is identified, never what it may then ask for.

## Token expectations

A publisher declaring this extension validates, at minimum, `iss` and `aud` against the values above
plus the token's own lifetime. Callers SHOULD mint short-lived tokens — a single request-response
exchange needs minutes, not hours — and SHOULD carry the calling identity in `sub`.

## Implementation

Morgana publishes this extension on every agent card it serves, and reads it on every card it
consults. See the [Morgana Handbook](../../../../Morgana-Handbook.html) and
[`ConfigurationAgentDirectoryService`](https://github.com/mdesalvo/Morgana/blob/main/Morgana/Morgana.AI/Services/ConfigurationAgentDirectoryService.cs).
