# behaviour-turn-continuation-operand

A turn that ends awaiting the user declares it through SetTurnContinuation alone, and never gates that wait behind a standalone exit button. This is the interplay of TurnContinuation, QuickReplyDoctrine and QuickReplyEscapeOptions read together.

- recorded: 2026-07-25 15:35:24Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 1
- runs: 5, passed: 5, required: 4

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 10,4 | 108857 | 1789 | 48760 | 0 |
