# The journey

What each phase of the prose revision moved, and at what price. The per-scenario files next to this
one carry the numbers, one row per phase; this file carries what they mean.

Read a row of a scenario file as a claim about the prose, not about the model: the model is held
fixed (same provider, same tiers, recorded in every file), the scenarios are held fixed except where
noted below, and what changes between phases is the text of `morgana.json` and `agents.json`.

## Phases

### `v0-vanilla` — the assessment

Morgana's prose as it stood before any revision: 0.25 wording throughout, plus the single rename A1
brought (`InteractiveToken` → `TurnContinuation`, the `#INT#` token replaced by the
`SetTurnContinuation` base tool). No consolidation, no cuts.

Eight scenarios, both dies. Six held, two did not:

- **`context-cross-agent` 2/5** — after a goodbye the agent kept the floor, so the third turn never
  reached ContractAgent; and in one run Contract asked again for a `userId` the shared-context
  registry already held.
- **`behaviour-conversation-closure` 1/5** — to an explicit goodbye the agent answered with the two
  closure buttons and stayed active, in four runs out of five. `ConversationClosure` carried the
  exception for exactly this case, as its last sentence, after the button payload.

The cost side is the more interesting half of `v0`. A single turn takes about **eight LLM round
trips** — one per tool call — and the composed system prompt plus the tool schemas is resent in full
on **each** of them: ~14k tokens for InventoryAgent. `context-no-invented-writes` therefore spends
133k input tokens on a turn whose entire content is *declining to invent a variable*. Verbosity is
billed per iteration of every tool loop, of every turn, forever. That is the argument for A2.

### `A2.1` — consolidate the global policies

Thirteen policies became ten, 15,935 characters became 11,820 (**−26%**), paid back on every round
trip described above.

`QuickReplyDoctrine` absorbed `ConversationClosure` and `QuickReplyEscapeOptions` — both existed
largely to re-explain it — with the two button payloads preserved verbatim and the three cases for
emitting Family B stated as *conditions* rather than as a rule followed by an exception buried behind
a JSON blob. `RichCardUsage` absorbed `RichCardAndQuickRepliesCombined`. `ToolGrounding` ceded its
quick-reply specifics to the Doctrine. `ContextHandling` and the two `Guidance` policies were carried
over verbatim: the overriding constraint, and the two names the code looks up by string.

What moved:

| scenario | v0-vanilla | A2.1 | input tokens |
|---|---|---|---|
| `context-cross-agent` | 2/5 ✗ | **5/5 ✓** | +4% |
| `behaviour-turn-continuation-operand` | 4/5 | **5/5** | **−14%** |
| `context-cycle-on-hit` | 5/5 | 5/5 | **−12%** |
| `context-cycle-on-miss` | 5/5 | 5/5 | **−17%** |
| `context-closed-vocabulary-monkeys` | 5/5 | 5/5 | +5% |
| `behaviour-conversation-closure` | 1/5 ✗ | see below | ~0 |

Restating the goodbye case as a condition is what fixed `context-cross-agent`. The token savings
land where the global layer weighs most; `context-cross-agent` costs *more* because it now reaches
its third turn instead of stalling on the second.

**One regression, caught and repaired.** Rewriting `TurnContinuation` I cut its worked example as
decorative. It was not: without it the agent asks for a value and closes the turn, and
`context-cycle-on-miss` fell to 3/5. The example is back — now four of them, deliberately of
different shapes (a code, a period, free text, a quantity), so the rule generalises past
"identification code", and with the parameter's scope explicitly decoupled from the decision: being
context-scoped or request-scoped governs how a value is *resolved*, never whether continuation is
due.

The lesson is worth carrying into the phases ahead: **a worked example inside a policy is
dispositive, not illustrative.** It does not get cut for verbosity.

**`behaviour-conversation-closure` held three different things**, and A2.1 separated them. The
buttons-at-goodbye defect is gone. Two of the assertions were wrong and were mine: the scenario
demanded the closure pair on a turn that may lawfully offer the invoices as choices, and forbade
`SetTurnContinuation(false)`, which the policy expressly permits. Rewritten, it immediately isolated
a **real defect that survives A2.1**: in 2 runs out of 5 the agent offers a Family-A list with no
escape pair appended, trapping the user inside it — precisely what the Doctrine prescribes against.

### `A2.2` — cut the base tool descriptions

`SetRichCard` went from 13,394 characters to 4,602 (**−66%**) with its contract intact: all eight
component types, every property, every enum value. What went was the compositional philosophy, the
"When NOT to use" blocks (the logical inverse of the "When to use" ones), the selection guide that
restated the dictionary, and one of three worked examples. `SetQuickReplies` lost the normative half
that the Doctrine already owns — including a **dead reference to the `ConversationClosure` policy**,
which A2.1 had folded away — keeping only the payload contract and the ordering detail (call it
before writing the text). `GetContextVariable` and `SetContextVariable` were not touched: A2.0.

The framework's fixed payload — global policies plus base tool schemas, resent in full on every one
of a turn's ~8 round trips — is now **20,906 characters against 35,040 at v0: −40%**.

| scenario | v0 | A2.1 | A2.2 | input tokens vs v0 |
|---|---|---|---|---|
| `behaviour-conversation-closure` | 1/5 ✗ | 1/5 ✗ | **4/5 ✓** | −27% |
| `context-cycle-on-miss` | 5/5 | 5/5 | 5/5 | **−42%** |
| `behaviour-turn-continuation-operand` | 4/5 | 5/5 | 5/5 | **−30%** |
| `context-cycle-on-hit` | 5/5 | 5/5 | 5/5 | −28% |
| `behaviour-rich-card` | 5/5 | — | 5/5 | −16% |
| `context-cross-agent` | 2/5 ✗ | 5/5 ✓ | **4/5 ✗** | −31% |
| `context-closed-vocabulary-monkeys` | 5/5 | 5/5 | **3/5 ✗** | −49% |

`behaviour-conversation-closure` meets its threshold for the first time. Two scenarios in the
blocking group do not, and both are worth reading carefully.

**`context-cross-agent` fell to 1/5, was repaired to 4/5, and is still short.** The cause was a real
hole in the prose that A2.1 had been masking: `ContextHandling` prescribed the cycle as "look before
asking, write on the answer", which says nothing about the case where the user volunteers the value
unprompted in their opening message. Billing then passed `userId` straight to its tool without ever
persisting it, the shared registry stayed empty, and Contract had to ask again three turns later.
Both `ContextHandling` and `SetContextVariable` now say it explicitly: however you come to learn a
context-scoped value — asked for or volunteered — you must write it. That moved the scenario from
1/5 to 4/5; one run in five still asks. The remaining gap is a candidate for
`ToolParameterContextGuidance`, which is the text injected next to the parameter itself.

**`context-closed-vocabulary-monkeys` fell from 5/5 to 3/5** while shedding half its tokens. Its two
failures are unlike each other: one run said "database" to the user outright, and one answered that
"some creature" was not found without ever naming the saimiri. This one is not yet diagnosed and
must not be waved through: it is the anti-invention scenario, and it is in the blocking group.

### Changes to the measuring instrument

- **A2.2** — the forbidden-term list was too coarse: bare `context` and `API` produced false
  positives on ordinary prose. It now names the mechanism unambiguously (`context variable`,
  `context store`, `GetContextVariable`, …). Rows before A2.2 were, on this check, slightly stricter
  than they should have been.

#### Earlier instrument changes

Recorded here because they make rows comparable, or not:

- **A2.1** — non-revelation moved from the LLM judge to a substring check. Morgana's persona speaks
  of magical ledgers and scrolls, and every phrasing of the proposition had the judge reading the
  metaphor as a data store by another name. The policy forbids *naming* the mechanism, a literal
  property, now measured literally. Rows before and after remain comparable on tokens; the pass
  rates are, if anything, slightly stricter and cheaper by one judge call per turn.
- **A2.1** — new `escapeOptionsWithPrimary` expectation, the complement of
  `noStandaloneEscapeOptions`. It only ever adds a way to fail, never removes one.
- **A2.1** — the farewell proposition no longer demands an explicit goodbye; a polite closing in
  character counts.

## Still open

- **Family-A lists without the escape pair** (`behaviour-conversation-closure`, 2/5). Real, and the
  Doctrine already says otherwise — a candidate for the next phase that touches it.
- `behaviour-rich-card` and `context-no-invented-writes` carry no `A2.1` row: both are
  InventoryAgent, the expensive die, and they will be re-measured at A2.3, which rewrites that
  agent's prose anyway.
