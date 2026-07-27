# behaviour-turn-continuation-operand

A turn that ends awaiting the user declares it through SetTurnContinuation alone, and never gates that wait behind a standalone exit button. This is the interplay of TurnContinuation, QuickReplyDoctrine and QuickReplyEscapeOptions read together.

- turns per run: 1
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 4/5 | 4 | 10.2 | 111853 | 1705 | 48505 |
| A2.1 | 2026-07-25 | 5/5 | 4 | 10.4 | 108857 | 1789 | 48760 |
| A2.2 | 2026-07-25 | 5/5 | 4 | 10.2 | 77848 | 1443 | 33814 |
| A2.3 | 2026-07-25 | 5/5 | 4 | 10.0 | 68623 | 1194 | 29932 |
| A2.5 | 2026-07-26 | 5/5 | 4 | 10.4 | 76548 | 1271 | 33734 |
| A2.5.2 | 2026-07-26 | 5/5 | 4 | 9.8 | 69342 | 1205 | 30521 |
| A2.5.5 | 2026-07-27 | 5/5 | 4 | 9.8 | 73805 | 1181 | 32805 |
| A2.6 | 2026-07-27 | 5/5 | 4 | 10.2 | 77656 | 1235 | 34308 |
