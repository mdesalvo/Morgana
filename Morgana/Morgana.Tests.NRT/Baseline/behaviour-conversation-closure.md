# behaviour-conversation-closure

Family B in both its wordings: escape appended to a choice list so the user is never trapped, and closure withheld when the user has already said goodbye. Also where the removal of the endsWithQuestion heuristic shows — nothing but the tool decides that the agent is done.

- turns per run: 2
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 1/5 | 4 | 15.0 | 164797 | 2116 | 73913 |
| A2.1 | 2026-07-25 | 1/5 | 4 | 17.0 | 169341 | 2561 | 74200 |
