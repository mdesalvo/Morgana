# context-cycle-on-miss

The full context cycle when the variable is not there yet: read before asking, ask in bare prose, write on the answer, then use the domain tool. This is the load-bearing scenario of the whole suite — if it regresses, context handling has regressed.

- turns per run: 2
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 16.8 | 171230 | 2420 | 78533 |
| A2.1 | 2026-07-25 | 0/5 | 5 | 16.2 | 142976 | 2408 | 65720 |
