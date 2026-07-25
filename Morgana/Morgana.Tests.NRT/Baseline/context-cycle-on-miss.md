# context-cycle-on-miss

The full context cycle when the variable is not there yet: read before asking, ask in bare prose, write on the answer, then use the domain tool. This is the load-bearing scenario of the whole suite — if it regresses, context handling has regressed.

- recorded: 2026-07-25 15:40:45Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 2
- runs: 5, passed: 0, required: 5

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 16,2 | 142976 | 2408 | 65720 | 0 |
