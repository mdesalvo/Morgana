# context-closed-vocabulary-monkeys

The anti-invention property. MonkeyAgent declares no context-scoped parameter, so its legal vocabulary is empty and any context access at all is an invented name — typically the query subject mistaken for a variable. The subject belongs to the domain tool, not to the registry.

- turns per run: 1
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 10.6 | 122727 | 1457 | 53664 |
| A2.1 | 2026-07-25 | 5/5 | 5 | 12.0 | 129326 | 1817 | 53201 |
| A2.2 | 2026-07-25 | 5/5 | 5 | 9.6 | 63695 | 1275 | 28748 |
| A2.3 | 2026-07-25 | 5/5 | 5 | 9.8 | 66383 | 1427 | 29928 |
| A2.5 | 2026-07-26 | 5/5 | 5 | 10.2 | 76121 | 1260 | 34375 |
