# behaviour-conversation-closure

A completed request offers the two closure buttons; a goodbye does not offer them again. The second turn is also where the removal of the endsWithQuestion heuristic shows: nothing but the absence of SetTurnContinuation decides that the agent is done.

- recorded: 2026-07-25 12:13:22Z
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)
- turns per run: 2
- runs: 5, passed: 1, required: 4

Per run, averaged — Morgana's own calls only, the judge excluded:

| llm calls | input tokens | output tokens | cache read | cache write |
|---:|---:|---:|---:|---:|
| 15,0 | 164797 | 2116 | 73913 | 0 |
