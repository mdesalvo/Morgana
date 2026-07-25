# behaviour-rich-card

Structured tool output is rendered as a rich card, never dumped as raw JSON into the text. Guards the RichCardUsage policy and the per-agent Formatting layer that instantiates it.

- recorded: 2026-07-25 14:52:26Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 1
- runs: 5, passed: 5, required: 4

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 9,8 | 208452 | 3125 | 86068 | 0 |
