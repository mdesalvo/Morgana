# behaviour-conversation-closure

Family B in both its wordings: escape appended to a choice list so the user is never trapped, and closure withheld when the user has already said goodbye. Also where the removal of the endsWithQuestion heuristic shows — nothing but the tool decides that the agent is done.

- turns per run: 2
- llm: Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)

Per run, averaged — Morgana's own calls only, the judge excluded:

| phase | recorded | passed | required | llm calls | input | output | cache read |
|---|---|---:|---:|---:|---:|---:|---:|
| v0-vanilla | 2026-07-25 | 1/5 | 4 | 15.0 | 164797 | 2116 | 73913 |
| A2.1 | 2026-07-25 | 1/5 | 4 | 17.0 | 169341 | 2561 | 74200 |
| A2.2 | 2026-07-25 | 4/5 | 4 | 16.2 | 119571 | 2408 | 48516 |
| A2.3 | 2026-07-25 | 5/5 | 4 | 13.4 | 85715 | 2173 | 34421 |
| A2.5 | 2026-07-26 | 5/5 | 4 | 13.0 | 90633 | 2079 | 38553 |
| A2.5.2 | 2026-07-26 | 5/5 | 4 | 12.6 | 87047 | 2162 | 37022 |
| A2.5.5 | 2026-07-27 | 4/5 | 4 | 13.6 | 101084 | 2162 | 41543 |
