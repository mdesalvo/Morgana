# context-cycle-on-miss

The full context cycle when the variable is not there yet: read before asking, ask in bare prose, write on the answer, then use the domain tool. This is the load-bearing scenario of the whole suite — if it regresses, context handling has regressed.

- turns per run: 2
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 16.8 | 171230 | 2420 | 78533 |
| A2.1 | 2026-07-25 | 0/5 | 5 | 16.2 | 142976 | 2408 | 65720 |
| A2.2 | 2026-07-25 | 5/5 | 5 | 16.0 | 98811 | 2434 | 44106 |
| A2.3 | 2026-07-25 | 5/5 | 5 | 16.4 | 108324 | 2517 | 47891 |
| A2.5 | 2026-07-26 | 5/5 | 5 | 16.8 | 122273 | 2280 | 54617 |
| A2.5.1 | 2026-07-26 | 5/5 | 5 | 16.4 | 114583 | 2288 | 51404 |
| A2.5.2 | 2026-07-26 | 5/5 | 5 | 16.0 | 106788 | 2311 | 48192 |
| A2.5.5 | 2026-07-27 | 4/5 | 5 | 15.8 | 114475 | 2184 | 51798 |
| A2.6 | 2026-07-27 | 4/5 | 5 | 15.6 | 109658 | 2318 | 49746 |
| A2.6.1 | 2026-07-27 | 1/5 | 5 | 14.4 | 98988 | 2206 | 44917 |
