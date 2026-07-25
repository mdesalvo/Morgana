# behaviour-rich-card

Structured tool output is rendered as a rich card, never dumped as raw JSON into the text. Guards the RichCardUsage policy and the per-agent Formatting layer that instantiates it.

- turns per run: 1
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 5/5 | 4 | 9.8 | 208452 | 3125 | 86068 |
| A2.2 | 2026-07-25 | 5/5 | 4 | 10.4 | 175126 | 3434 | 68349 |
| A2.3 | 2026-07-25 | 5/5 | 4 | 10.8 | 165576 | 3251 | 66105 |
