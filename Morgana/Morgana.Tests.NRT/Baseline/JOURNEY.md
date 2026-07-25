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

**Both blocking reds were repaired; A2.2 closes green.** Final state: `context-cross-agent` 5/5,
`context-closed-vocabulary-monkeys` 5/5 (63,695 input tokens against 122,727 at v0, −48%).

**`context-cross-agent` fell to 1/5 and was repaired in two steps.** The cause was a real
hole in the prose that A2.1 had been masking: `ContextHandling` prescribed the cycle as "look before
asking, write on the answer", which says nothing about the case where the user volunteers the value
unprompted in their opening message. Billing then passed `userId` straight to its tool without ever
persisting it, the shared registry stayed empty, and Contract had to ask again three turns later.
Both `ContextHandling` and `SetContextVariable` now say it explicitly: however you come to learn a
context-scoped value — asked for or volunteered — you must write it. That moved the scenario from
1/5 to 4/5. The last run was closed by putting the same obligation where the decision is actually
made: `ToolParameterContextGuidance`, the line injected beside every context-scoped parameter, held
the read half of the cycle ("look before asking") while the write half lived two policies away. It
now carries both. 5/5.

**`context-closed-vocabulary-monkeys` fell to 3/5, and the fault was the scenario's.** The MCP
server was reachable and unbanned — checked directly — but its catalogue is indexed by common name:
`get_monkey("Saimiri")` returns null, and the genus appears only inside the "Squirrel Monkey"
record. The scenario was asking about a subject the tool cannot resolve, which sent the agent down a
not-found branch (where one run volunteered the word "database" to the user) and had an
anti-invention test measuring synonym resolution instead.

The subject is now a proper name the catalogue holds — Sebastian — which is also the sharper test:
a proper name is exactly the string a confused model passes to `GetContextVariable`. One detour on
the way: "Tell me about Sebastian" alone gives the classifier no domain signal at all, lands on
`other`, and the scenario collapsed to 0/5 with a single LLM call and 286 tokens — the agent was
never reached. Phrased as "a monkey named Sebastian" it holds 5/5.

The lesson pairs with A2.1's: **a scenario that fails is a claim about the prose only once you have
ruled out the scenario itself.** Here the tool contract, the classifier and the test wording all had
to be excluded before the prose could be accused — and it turned out innocent.

### `A2.3` — retarget the layers in agents.json

InventoryAgent carried the same dispositive sequence twice: as prose in `Instructions` and again as
six numbered steps inside `Formatting`. The two layers were rebuilt on the model — `Instructions` is
what you must do and in what order, `Formatting` is how you present it — and the local rewrite of the
Quick Reply Doctrine was deleted outright, being global since A2.1. Together **12,682 -> 5,526
characters, −56%**, with the two gates (seal word proves ownership, explicit yes authorises), the
live-stock rule, the non-existent capabilities and both button payloads all preserved.

Billing and Contract had `Instructions` consisting solely of the shared verbatim that restates
ToolGrounding; they now carry their actual behavioural constraint, which is that their tools are
read-only. Presentation directives were removed from every tool description — "YOU MUST present this
using SetRichCard…" is Formatting, and each agent's Formatting already holds the card shape per
tool; what stays in a tool description is the data contract and whether it is informative or
dispositive.

**agents.json prose: 31,119 -> 22,346 characters, −28%.** Combined with A2.1 and A2.2, an
InventoryAgent turn now carries roughly half the fixed payload it carried at v0.

The phase first landed with three scenarios short. Seven of eight now hold; one does not, and it is
left open **on purpose** — see the framework gap below.

| scenario | v0 | A2.2 | A2.3 first | A2.3 closed |
|---|---|---|---|---|
| `context-cycle-on-miss` | 5/5 | 5/5 | 5/5 | 5/5 |
| `context-cycle-on-hit` | 5/5 | 5/5 | 5/5 | 5/5 |
| `behaviour-conversation-closure` | 1/5 ✗ | 4/5 | 4/5 | 5/5 |
| `behaviour-turn-continuation-operand` | 4/5 | 5/5 | 4/5 | 5/5 |
| `behaviour-rich-card` | 5/5 | 5/5 | 5/5 | 5/5 |
| `context-no-invented-writes` | 5/5 | — | 4/5 ✗ | **5/5** |
| `context-closed-vocabulary-monkeys` | 5/5 | 5/5 | 4/5 ✗ | **5/5** |
| `context-cross-agent` | 2/5 ✗ | 5/5 | 2/5 ✗ | **3/5 ✗ (open)** |

**`context-cross-agent` — the diagnosis was not where the phase's diff was.** Turn 1 wrote `userId`
correctly; ContractAgent simply never called `GetContextVariable` at turn 3 and opened by asking.
The first repair attempt was to say so louder in `ContextHandling` (P0) — that the store is
conversation-wide and outlives any single agent. It failed twice over: `context-cross-agent` stayed
red, and `behaviour-turn-continuation-operand` fell from 4/5 to 2/5, the agent now interrogating the
user *about the customer code* instead of asking which invoice they meant. Every word was reverted
and the rule was placed instead where the layer model puts it — in ContractAgent's own
`Instructions`, as the domain constraint that all three of its tools are keyed to one customer, so
the first act of any request is to establish whose contract is being read, by checking rather than
by opening with a question.

That helped and did not settle it: across this phase the scenario measured 2/5, 3/5, 5/5, 3/5, 3/5,
which is not an unstable 5/5 but a behaviour sitting somewhere near 70-80% **with the domain patch
already in place**. The same sentence given to BillingAgent, by contrast, took
`context-cycle-on-miss` from a stable 4/5 — four consecutive measurements, on prose proven
byte-identical to an earlier 5/5 — back to 5/5, without shortening the blanket on any other Billing
scenario. One agent's prose can carry the rule; the other's cannot, and that asymmetry is the
signal. Writing yet more into Contract would be the patch that hides the library's defect, so the
scenario stays red and becomes the acceptance test for the framework fix described below.

The lesson is the sharpest of the three phases so far: **a P0 policy is the most expensive place in
Morgana to state anything**, because every agent renders it on every round trip and a sentence
aimed at one agent's failure re-aims every other agent's attention. When a defect belongs to one
agent, it is fixed in that agent's prose.

**The blanket.** The two layers are complementary and the model's attention is a fixed budget:
pull the blanket toward the policies and it comes off the agents, and the reverse. Every repair in
this phase demonstrated it, and always in the same shape — *the red does not appear where you
wrote*. A change to a global policy must therefore be measured against the agents, and a change to
one agent's prose against **every** scenario touching that agent, not only the one being repaired.
The order of priority between the two is not symmetric, though: the framework and its policies are
the product and must be rock-solid, because every third party will bring their own domain agents to
them. Agents are malleable, and a consequential patch there to bring behaviour back into line is
legitimate. A patch that hides a policy gap is not.

**Where the policy layer is actually thin**, found while repairing this phase and deliberately left
for the framework phase rather than improvised here. The rule "check the context before asking the
user" exists twice, and neither copy sits where the failure happens:

- `ToolParameterContextGuidance` is injected beside each context-scoped parameter — nominally the
  point of decision — but a parameter description is read once the model is already invoking that
  tool, whereas the failing path is deciding to ask the user *instead of* calling it. The guidance
  is on a branch the defect never takes.
- `ContextHandling` (P0) is on a branch the model always reads, but it is furthest from the moment
  of choice, and strengthening it is exactly what shortened the blanket above.
- Worse, `FormatGlobalPolicies` renders **every** policy, so `ToolParameterContextGuidance` also
  appears in the composed system prompt, where its opening words — "BEFORE INVOKING THIS TOOL" —
  have no referent at all: there is no current tool there. It is an instruction pointing at nothing,
  and the same ~470 characters are then re-injected once per context-scoped parameter of every tool,
  on each of a turn's round trips.

That BillingAgent and ContractAgent both had to state the rule in their own `Instructions` is the
symptom, not the cure: a domain author should inherit that guarantee. The framework phase should
(a) treat the two `Guidance` policies as injection templates and stop rendering them as policies,
and (b) give the *tool's own* description a framework-owned line naming its context-scoped
parameters — the tool description being what the model weighs when choosing whether to call the tool
at all. **Acceptance test: `context-cross-agent` returns to 5/5, and the identity sentences in
BillingAgent and ContractAgent become removable without any scenario falling.** If they are still
needed afterwards, the fix was the wrong one.

**`context-no-invented-writes` — a real defect, and not the one the structure was watching.** No
context write ever happened; the agent simply *told* the user it had remembered ("I've etched your
fondness into memory"). The discipline held in action and failed in speech. A first fix enumerating
the forbidden verbs was routed around within one run — "say the word 'roses' again and I shall tend
to that wish anew" — which is what a list of words earns against a language model. Restated as a
principle and moved to `ToolGrounding`, its actual home (claiming a capability you do not have is
not context handling), it holds: the rule binds claims exactly as it binds offers, and nothing a
user says about themselves outlives the request unless a tool contract declares it.

**`context-closed-vocabulary-monkeys` — the instrument again.** Two successive judge propositions
failed on content rather than behaviour: "an actual monkey" had the judge disputing a catalogue
whose Sebastian is a hand puppet, and "specific details" had it reading Morgana's grimoire metaphors
as vagueness. The judge was removed. The property is literal — the subject reached the domain tool
and the registry was never touched — and is now `toolsCalled: [get_monkey]` beside
`noContextAccess`, which is deterministic, free, and cannot argue.

**What the phase cost on the fixed payload: nothing.** Every repair that reached for the framework
layer was reverted, and `morgana.json` closes the phase byte-identical to how it entered it — the
whole cost landed in the domain layer, where it belongs and where it is paid only by the agent that
needs it. Standing totals: framework fixed payload **35,040 -> 21,289, −39%**; `agents.json`
**31,119 -> 23,254, −25%**.

### Changes to the measuring instrument

- **A2.3** — `LlmJudge` had a fallback for a judge that returns an unusable answer, with a comment
  explaining that a proposition which stops being evaluated is coverage lost without anyone
  noticing. A `when (attempt == 1)` filter on the catch made that fallback **unreachable**: the
  second attempt's exception escaped into `ScenarioRunner`, which aborted the whole run and printed
  `run aborted: KeyNotFoundException` — a line that reads like a broken scenario and is not one. It
  cost a paid run each time it fired, and it fired at least once inside a measurement that was then
  read as a prompt regression. `GetProperty("holds")` is now a `TryGetProperty`, and the catch no
  longer excludes the final attempt. **Rows before A2.3 may understate pass rates by one run.**
- **A2.3** — `context-cross-agent` turn 1 now asserts `contextWrites: [userId]`. Without it the
  scenario noticed a missing write only two turns later, at ContractAgent, and reported the wrong
  agent as guilty. It only adds a way to fail.
- **A2.3** — `context-no-invented-writes` says "from your greenhouse". Without a domain word the
  message is a bare preference, the classifier lawfully files it under `other`, and the run never
  reaches InventoryAgent — measuring routing instead of anti-invention. The temptation under test,
  the urge to mint a key for "roses", is untouched.
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

- **`context-cross-agent` at 3/5**, blocking, and open by decision rather than by omission: its
  cause is the framework gap described under A2.3, and the next phase is the framework one, with
  this scenario as its acceptance test.
- **The prose is now tuned partly against these scenarios**, and that is a debt, not an achievement.
  The identity rule in BillingAgent and ContractAgent, and InventoryAgent's clause about having
  nowhere to record what a customer likes, were all written after watching a scenario fail. Each is
  defensible on its own terms — a real constraint, in the right layer — but a suite is only evidence
  while the prose is not written to it, so a coming phase should add coverage it has never seen
  rather than deepen what it already has.
- **`behaviour-conversation-closure` reached 5/5** at the close of A2.3, so the Family-A-list defect
  that stood open since A2.1 (a choice list emitted with no escape pair, trapping the user) was not
  observed again. It was never repaired deliberately, so treat it as unproven rather than fixed.
