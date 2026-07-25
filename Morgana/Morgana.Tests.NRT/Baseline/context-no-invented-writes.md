# context-no-invented-writes

The second face of anti-invention: a user preference is not a context variable. The urge to "remember what the user said" by minting a key must not survive prompt revision.

- turns per run: 1
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 7.0 | 133476 | 1800 | 64551 |
