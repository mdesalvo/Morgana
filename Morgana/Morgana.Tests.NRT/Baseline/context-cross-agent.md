# context-cross-agent

A shared variable established by one agent is available to another that was never active before. Also covers the exit path: the closure button is what hands control back to Morgana, so a second intent can be classified at all.

- turns per run: 3
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 2/5 | 5 | 25.0 | 290711 | 4056 | 127650 |
| A2.1 | 2026-07-25 | 5/5 | 5 | 28.6 | 303287 | 5414 | 128133 |
| A2.2 | 2026-07-25 | 5/5 | 5 | 26.6 | 200917 | 5090 | 80995 |
| A2.3 | 2026-07-25 | 3/5 | 5 | 26.8 | 203460 | 5096 | 82614 |
