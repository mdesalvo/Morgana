# behaviour-turn-continuation-operand

A turn awaiting typed input stays in service through SetTurnContinuation alone, with no buttons of any kind beside it — neither a choice list nor the escape options. This is the interplay of TurnContinuation, QuickReplyDoctrine and QuickReplyEscapeOptions read together.

- recorded: 2026-07-25 12:14:42Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 1
- runs: 5, passed: 0, required: 4

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 11,4 | 131090 | 1697 | 60054 | 0 |
