# behaviour-turn-continuation-operand

A turn that ends awaiting the user declares it through SetTurnContinuation alone, and never gates that wait behind a standalone exit button. This is the interplay of TurnContinuation, QuickReplyDoctrine and QuickReplyEscapeOptions read together.

- recorded: 2026-07-25 14:41:58Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 1
- runs: 5, passed: 4, required: 4

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 10,2 | 111853 | 1705 | 48505 | 0 |
