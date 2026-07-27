# context-cycle-on-hit

The other half of the cycle: once a context variable is known, the agent uses it and does not ask again. Hesitation here is as much a regression as an invented name.

- turns per run: 2
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 5 | 18.4 | 220468 | 2935 | 97011 |
| A2.1 | 2026-07-25 | 5/5 | 5 | 20.6 | 229399 | 3176 | 99640 |
| A2.2 | 2026-07-25 | 5/5 | 5 | 19.6 | 159667 | 3163 | 64688 |
| A2.3 | 2026-07-25 | 5/5 | 5 | 17.0 | 123477 | 2497 | 50884 |
| A2.5 | 2026-07-26 | 5/5 | 5 | 17.6 | 131588 | 2594 | 54617 |
| A2.5.1 | 2026-07-26 | 5/5 | 5 | 17.6 | 133301 | 2893 | 54842 |
| A2.5.2 | 2026-07-26 | 5/5 | 5 | 18.2 | 140087 | 2670 | 58070 |
| A2.5.5 | 2026-07-27 | 5/5 | 5 | 16.8 | 135683 | 2448 | 57202 |
| A2.6 | 2026-07-27 | 5/5 | 5 | 18.0 | 152073 | 2564 | 63709 |
