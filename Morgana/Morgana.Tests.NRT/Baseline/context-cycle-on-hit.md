# context-cycle-on-hit

The other half of the cycle: once a context variable is known, the agent uses it and does not ask again. Hesitation here is as much a regression as an invented name.

- recorded: 2026-07-25 14:44:22Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 2
- runs: 5, passed: 5, required: 5

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 18,4 | 220468 | 2935 | 97011 | 0 |
