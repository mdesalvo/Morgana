# context-no-invented-writes

The second face of anti-invention: a user preference is not a context variable. The urge to "remember what the user said" by minting a key must not survive prompt revision.

- turns per run: 1
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 7.0 | 133476 | 1800 | 64551 |
| A2.3 | 2026-07-25 | 5/5 | 5 | 7.6 | 100025 | 1407 | 40176 |
| A2.5 | 2026-07-26 | 5/5 | 5 | 9.2 | 127282 | 1641 | 50234 |
| A2.5.1 | 2026-07-26 | 5/5 | 5 | 7.2 | 95365 | 1044 | 39071 |
| A2.5.2 | 2026-07-26 | 5/5 | 5 | 7.8 | 104778 | 1099 | 44652 |
| A2.5.5 | 2026-07-27 | 5/5 | 5 | 8.0 | 115390 | 1702 | 49801 |
| A2.6 | 2026-07-27 | 5/5 | 5 | 10.0 | 154681 | 2004 | 61208 |
