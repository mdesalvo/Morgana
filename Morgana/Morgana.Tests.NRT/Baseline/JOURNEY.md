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

### `A2.5` — the framework prompt

The first revision of the `Morgana` prompt itself. Four blocks moved, all in `morgana.json`:

**`Target`** stopped being a role description and became the composed prompt's **preamble**. It had
been the didactic boilerplate of every agentic tutorial — "you are a digital assistant" — sitting in
the primacy slot and promising a capability (*"solve problems through the support scenarios you can
handle"*) that `ToolGrounding` then spent a clause defending against. It now names the two layers,
says this one governs how a turn is formed and the domain one governs what the conversation is
about, and settles precedence. It promises nothing.

**`Instructions`** carried the **order of a turn**: resolve inputs → call the domain tool → decide
presentation → write the text once. This is the load-bearing change of the phase, and it was
predicted before it was measured. `context-cycle-on-miss` had been the blocking red since A2.1, and
the diagnosis was that ask-before-looking is not a **missing rule** — the rule already existed in
three places — but a **sequencing** failure: no policy states the order, because each governs one
aspect of a turn. The clause that closes it says why the order is not stylistic: the whole turn
reaches the user as one message, so a question composed early and a tool result that lands later
arrive side by side, and the user reads a demand for something the assistant already had.

**`Personality`** was defining the character *by a procedure* — "uses her magic tomes to formulate
potions and spells" — so P6's counter-clause was fighting the persona rather than the model. The
magic now "reaches the user as a result and never as a procedure they are made to watch". Youth also
stopped licensing hedging: it shows as warmth and modesty, never as disclaiming.

**`MandatoryTextualResponse` (P6)** was rewritten whole. It had contained both the prohibition and
its own licence — *"if you have nothing new to add, briefly say what you just did"* was literally
generating "let me see if I have your userId… oh yes" — and it handed the model the framework's
internal vocabulary through a tool taxonomy (presentation / context / domain / MCP tools). Both are
gone. What replaced them draws the distinction the phase turns on: **the rule is about the subject,
never the register.** Morgana is a witch and will always speak figuratively; a metaphor *about her
own machinery* is still machinery, disclosed in better clothes.

`Formatting` lost an empty opening line. The rest was defended as scar tissue.

**The policy block also changed shape, and that part is code.** Each policy used to open with its
own "CRITICAL RULE ABOUT …" prefix — repeated eight times it discriminated nothing, since every
rendered policy is critical, and it restated the `Name` the line already carried. It is now said
once in `GlobalPoliciesHeader`, above the list. The matching `GlobalPoliciesFooter` is not
symmetry: the per-policy prefixes each ended with their own rule, whereas an opening claim about
what follows has no end, and what follows the list is the framework's own Instructions and
Formatting and then the whole domain layer — none of it binding in that sense. The header also
gives "the critical rules", cited inside the Morgana Instructions, an antecedent; the footer is
what stops that antecedent from swallowing everything after it.

At the same time the three tool-guidance entries were reclassified `Type: "Injection"` and are no
longer rendered at all. They were never policies: `MorganaToolAdapter` splices them into the
description of the tool or parameter they govern, at the point where the model decides. Printed in
a system prompt they are instructions with no referent — "BEFORE INVOKING THIS TOOL" names no tool
there — and the framework paid for them on every round trip of every turn. `ToolDescriptionContext‑
Guidance` is new, and its placement is the point: a *parameter* description is read once the model
is already invoking the tool, whereas the failure it guards — opening by asking the user for a
value the store already holds — happens one step earlier, when the model weighs the tool at all.
That emptied the `Operational` tier, whose last inhabitant (`RichCardUsage`) was in no way
secondary and was promoted rather than left alone in a tier that no longer meant anything.

| scenario | A2.3 | A2.5 |
|---|---:|---:|
| `context-cycle-on-miss` | 5/5 | **5/5** |
| `context-cycle-on-hit` | 5/5 | **5/5** |
| `context-closed-vocabulary-monkeys` | 5/5 | **5/5** |
| `context-no-invented-writes` | 5/5 | **5/5** |
| `context-cross-agent` | 3/5 | **4/5** |

**The blocking red closed.** `cycle-on-miss` holds at 5/5 on the stated mechanism, and the payload
grew to buy it: composed framework layer **14,210 -> 14,909 chars, +4.9%** — the first phase since
v0 to add rather than cut, deliberately, because what was missing was a sequence no cut could supply.

**The domain layer paid for it.** Once the framework states the order of a turn, the per-agent
restatement of it is a duplicate, so BillingAgent and ContractAgent lost the clause *"your first act
on any request is to establish whose invoices you are reading — by checking, never by opening with a
question"*. That is the structural move this journal keeps arguing for: one rule, stated once, at
the layer that owns it. The `userId` parameter descriptions were rewritten in the same pass, from
the mechanical *"Unique alphanumeric identifier … Retrieved from conversation context"* — which
described the plumbing to a reader who must not think about plumbing — to what the customer would
call it, plus the fact that keys every tool in the book: *one customer, one code*. That also
retires part of the tuning debt recorded under **Still open**: the identity rule was written after
watching a scenario fail, and it is now gone rather than deepened.

**What `context-cross-agent` still costs, and it is the price of that trade.** One run in five,
ContractAgent answers *"I need to know your user identification so I can retrieve your contract
details"* on a conversation where BillingAgent has already written `userId` to the shared registry —
and the trace shows `context: (none)`: `GetContextVariable` was **not called at all**. The lookup
did not fail, it did not happen.

The two facts belong together. The framework rule replaced the agent rule, and it holds where the
agent is the one the conversation started with — `cycle-on-miss` went from the suite's oldest red to
5/5 on exactly that. It does not hold as reliably for an agent activated for the first time mid-
conversation, which is the one case the deleted clause used to cover locally. So the residual 4/5 is
not the mysterious framework gap named under A2.3: it is a duplicate that was removed on purpose,
paying off in four scenarios and coming due in one. Whether the answer is to restore the clause in
the domain layer, or to make the framework's turn order survive an agent's first activation, is the
decision the next phase has to make — and it should be made on that framing, not by patching the
symptom.

**The instrument mistake, and what it cost.** This phase also added a new `judgeNot` proposition to
the blocking group — against the assistant explaining its own workings — *in the same measurement as
the prose change*. That was an error of method, and it is recorded below rather than here because
the proposition has been **reverted**: it never produced a comparable number and does not belong to
this phase's result. The table above is read from the structural signals of the final run, where
exactly one of fifteen runs failed structurally. The A2.5 rows in the three affected scenario files
were **corrected by hand** to that reading: as written by the harness they said 1/5, 2/5 and 3/5,
counting failures against an assertion that no longer exists. Token columns are untouched — those
were measured.

**The behavioural group was re-run, and did not move.** `behaviour-conversation-closure`,
`behaviour-rich-card` and `behaviour-turn-continuation-operand` all hold at 5/5, exactly as at A2.3.
This was the phase's one real exposure: `Personality` and P6 are global, they were rewritten after
that group's last measurement, and A2.1 had already shown once that pulling the blanket toward the
framework layer takes it off the agents — `turn-continuation-operand` fell from 4/5 to 2/5 on a
policy change that looked unrelated. It did not happen this time. Token cost is flat within noise,
and `rich-card` is 8% cheaper on input.

### `A2.5.1` — the fact the prompt never carried

The first phase that is not a prose revision. A2.5 left one red and framed the decision as *restore
the clause in the domain layer* vs *make the framework's turn order survive first activation*. Both
options assumed the defect was a **rule** not reaching far enough. It was not: it was a **fact** that
no layer ever stated.

**The mechanism.** `MorganaAIContextProvider.ProvideAIContextAsync` — the framework's designated
pre-invocation hook, the one place that runs per turn and can see session state — returned an empty
`AIContext`, marked *"Reserved for future use"*. Meanwhile `MorganaAgent` hydrates the shared registry
into that very provider immediately **before** the call. So at the failing turn the framework held
`userId` and told the model nothing about holding it.

**Why only one scenario ever showed it.** Chat history is per-agent: each `AgentSession` carries its
own. An agent activated for the first time mid-conversation therefore opens on an **empty transcript**
and has never seen the value. Everywhere else in the suite the value sits in the agent's own visible
history, so `GetContextVariable` is redundant and the model passes for the wrong reason. Turn 3 of
`context-cross-agent` is the **only turn in the whole suite** where the context registry is the sole
route — one true test, sampled five times, while three phases moved prose that could not touch its
cause. That is what the 3/5 → 4/5 drift was hiding.

**The fix, and why it is not a sixth restatement.** A fourth `Type: "Injection"` entry,
`HeldContextDeclaration`, spliced per turn by the provider and resolving `((held_variables))` to the
names the session holds — minus the framework's ephemeral keys, and injecting **nothing** when it
holds none. The first objection to answer was duplication: `ToolDescriptionContextGuidance` already
names `userId` on the very tool, so the model was never guessing blindly. The distinction is
structural rather than rhetorical, and it is enforced by where the code runs — tool descriptions are
built **once**, at agent creation, so that template can only ever state the **contract** (*"this tool
takes a userId"*, true against an empty store); it can never state the **state** (*"userId is held
right now"*), which nobody knows at build time. It also extends the placement ladder A2.5 climbed one
rung: a parameter description is read while the tool is already being invoked, a tool description
while the tool is weighed, and this before **any** tool is weighed — which is precisely where the
failure happened, the failing runs calling `SetTurnContinuation` alone and never selecting the domain
tool whose description carried the guidance.

The text carries **zero instructions**, deliberately. "Look a context-scoped value up before asking"
is already stated five times (P0, the framework `Instructions`, both tool/parameter templates, and
`GetContextVariable`'s own description); a sixth would add contradiction surface and no information.
Every clause states a fact, explains why the agent's own history is silent about it, or restricts the
note itself — *names only, values not shown*, so the model cannot claim a value it never read, and
*addressed to you alone*, guarding the leak the list itself creates.

| scenario | A2.3 | A2.5 | A2.5.1 |
|---|---:|---:|---:|
| `context-cross-agent` | 3/5 | 4/5 | **5/5** |
| `context-cycle-on-hit` | 5/5 | 5/5 | **5/5** |
| `context-cycle-on-miss` | 5/5 | 5/5 | **5/5** |
| `context-no-invented-writes` | 5/5 | 5/5 | **5/5** |
| `context-closed-vocabulary-monkeys` | 5/5 | 5/5 | *not re-run* |

`monkeys` was excluded by proof rather than by budget: it declares no context-scoped parameter, so it
can never hold a variable and the injection can never fire there.

**What 5/5 establishes, and what it does not.** Turn 3 asserts `toolsCalled: [GetContractDetails]`
and `judgeNot: "asks the user for their customer code"`. Five times out of five ContractAgent called
the domain tool without asking — and it could not have had the code from anywhere else, since its
history is empty and the injected note carries names, not values. So the context was read in all five
runs. That is an **inference**, not an assertion: the scenario never checks `contextReads`. Recorded
below as an instrument gap.

**Cost.** The only tight comparison is `cycle-on-hit`, where the LLM call count is identical at 17.6:
**+1.3% input tokens**. `cross-agent`'s +10.7% is not overhead — at A2.5 one run in five died at turn
3 after 5 calls instead of 13 and pulled the average down; now all five do the whole work. The other
two scenarios fell (−6% and −25%), which on five live runs is variance, not saving.

**What it opens.** The apparatus that existed to compensate for the missing fact is now testable for
removal — five statements of one rule, the first candidate being `ToolParameterContextGuidance`,
which near-verbatim repeats the tool-level template and is the only one paid per-parameter, per-tool,
on every round trip.

Verified before spending anything: a capturing `IChatClient` confirmed off-line that the note reaches
`ChatOptions.Instructions` (in the recency slot, after the domain layer), that an empty session
injects nothing, and that seeded ephemeral keys are filtered out.

### `A2.5.2` — a cut that turned out to be free, and why

The plan was clean. A2.5.1 supplied the fact five restatements existed to compensate for, so the
apparatus became testable for removal, and `ToolParameterContextGuidance` was the obvious first
candidate: it repeats clause for clause — write half included — what P0, the framework `Instructions`
and `ToolDescriptionContextGuidance` already say, and it is the only one paid **per-parameter,
per-tool, on every round trip**. 661 characters, 112 words, times three context-scoped parameters on
Billing and Contract, two on Inventory.

The whole suite was run, all 11 tests, 8 scenarios:

| scenario | A2.5.1 | A2.5.2 |
|---|---:|---:|
| `context-cross-agent` | 5/5 | **5/5** |
| `context-cycle-on-hit` | 5/5 | **5/5** |
| `context-cycle-on-miss` | 5/5 | **5/5** |
| `context-no-invented-writes` | 5/5 | **5/5** |
| `context-closed-vocabulary-monkeys` | — | **5/5** |
| `behaviour-conversation-closure` | — | **5/5** |
| `behaviour-rich-card` | — | **5/5** |
| `behaviour-turn-continuation-operand` | — | **5/5** |

**The token columns refused to cooperate, and that is what gave it away.** `cycle-on-hit`, the tight
comparison, went **up** 5.1%. `no-invented-writes` up 9.9%, `cross-agent` down 3.6%, `cycle-on-miss`
down 6.8%. And `monkeys` — which declares no context-scoped parameter and therefore cannot have been
touched by the cut — moved 4.9%. That is the noise floor at five runs, and it swamps a change of this
size; but it also left open whether the cut had shipped at all. Checking that, off-line, is what
opened the real finding.

**Parameter descriptions have never reached the model.** `MorganaToolAdapter` assembles the enriched
per-parameter descriptions and hands them to `AIFunctionFactoryOptions.AdditionalProperties`, which is
documented as *"additional values to store on the resulting `AITool.AdditionalProperties` property …
arbitrary information about the function"* — metadata on the function object, never part of the
emitted JSON schema. MEAI takes parameter descriptions from the delegate's `[Description]` attributes,
and no `MorganaTool` method carries one. Driving the real path off-line — real policies, real adapter,
real `CreateFunction` — emits:

```json
{"type":"object","properties":{"userId":{"type":"string"},"count":{"type":"integer"}},"required":["userId","count"]}
```

Bare. It has been so since commit `a116fae`, 2025-12-20, whose title states the belief being
contradicted: *"Exploit AIFunction.AdditionalProperties to convey tool's parameters with their name,
description and scope"*. Seven months.

**What this costs, and what it does not.** The model still receives the system prompt entire, the tool
name, the tool **description** — which is why `ToolDescriptionContextGuidance` works and shows up in
the probe intact — and every parameter's name, type and required flag. Nothing was flying blind, and
`userId` is a fairly self-documenting name. What was lost is the authored prose: every parameter
description in `agents.json`, including A2.5's rewrite of `userId` from mechanical plumbing to *one
customer, one code*, which this journal recorded as part of that phase and which never shipped. MCP
tools take the same path, so descriptions authored by a remote server are parsed carefully by
`MCPToolAdapter` and then dropped too.

**The sharpest consequence is historical.** `ToolDescriptionContextGuidance` is one phase old — A2.5
created it. Before that, the only place naming which parameters are context-scoped was the
parenthetical inside P0: *"parameter names declared with 'Scope: context' in your tool contracts (for
example 'userId')"*. For seven months the entire scope contract travelled on one example inside one
policy, because the channel built for it did not run. A2.5 argued the promotion from parameter to tool
on placement grounds; it turns out to have been the only working channel, which is luck, not method.

**So this phase measured nothing about its own hypothesis.** 11/11 says nothing regressed — worth
having, since the phase also carries the `morganaPolicies` and `GlobalPolicy.Templates` refactors — but
the cut was a no-op on the wire and the redundancy argument remains unmeasured. **The cut is kept
anyway**, and for a stronger reason than the one it was made for: when the schema defect is repaired,
112 words of duplicated rule must not reappear on every context-scoped parameter of every tool. Kept
cut, the repair ships only the authored descriptions.

### `A2.5.3` — the descriptions arrive

The repair A2.5.2 found and deferred. MEAI already owns the hook, so nothing was invented:
`AIJsonSchemaCreateOptions.ParameterDescriptionProvider` is a `Func<ParameterInfo, string?>` that
`AIFunctionFactory` calls once per parameter **while it generates the schema**, and whose return value
becomes that parameter's `description` keyword. `CreateFunction` keeps assembling exactly the same
per-parameter text it always assembled and hands it there instead of to
`AIFunctionFactoryOptions.AdditionalProperties`. One method body; no configuration moved, no contract
changed, no package moved — the hook has existed since `Microsoft.Extensions.AI` 10.4.0 and the repo
has been on 10.6.0 since before 0.25.

Driven off-line against the real policies and the real adapter, the schema that was bare now reads:

```json
"userId": { "description": "The customer's own identifying code, whatever they call it — customer
code, account number, client id (e.g. 'P994E'). Every tool here is keyed to it: one customer, one
code.", "type": "string" }
```

That sentence is A2.5's rewrite of `userId`. It was authored precisely against the failure of a model
casting about between *user code*, *customer code*, *user identifier* — and until this commit it had
never once been sent.

**Both registration paths were proved, not assumed.** Domain tools register a plain delegate; MCP tools
register a `DynamicMethod` built by `MCPToolAdapter` with explicit `DefineParameter` calls. The probe
drives both, and `ParameterInfo.Name` survives IL emission, so the provider matches MCP parameters by
name like any other — a remote server's own authored descriptions now travel the whole way instead of
being parsed and dropped.

**One prose defect surfaced the moment anything shipped.** The join was `$"{description}. {guidance}"`,
which produced `retrieve (1-10).. Use the value directly…` on every request-scoped parameter. The fix
belongs to the prose contract, not to the code: an authored description is a finished sentence and
closes itself, so the join is a single space and every request-scoped description in both configuration
layers was checked to end in terminal punctuation (all 15 do). A helper that inspected the last
character and supplied the missing full stop was written first and then removed — that is a bonification
codicil, and it would have let a badly-formed description into the layer by quietly repairing it.

**The payload, measured statically** — the only honest way at this size, per A2.5.2's lesson. Added
characters per agent, base tools plus domain tools:

| agent | domain params | added chars | ≈ tokens |
|---|---:|---:|---:|
| base tools (every agent) | 6 | 1 335 | 333 |
| `Billing` | 6 | 2 815 | 703 |
| `Contract` | 5 | 2 614 | 653 |
| `Monkeys` (MCP) | 0 | 1 335 | 333 |
| `Inventory` | 12 | 5 359 | 1 339 |

**This is the largest single addition of the whole A2 arc**, and it runs in the opposite direction to
every phase before it: A2.1–A2.3 and A2.5.2 all cut. The text is authored information rather than
restated rule, which is the kind that earns its place — but it lands on the blocking context group, and
`SetQuickReplies` alone contributes a 430-character format example the model has never seen. The
measurement is what settles it, and until the suite is re-run at this phase nothing here is a result.

**Backport.** The change is one method body in `MorganaToolAdapter`, with no dependency on anything else
in 0.26 — it cherry-picks onto `main` as it stands.

### Changes to the measuring instrument

- **A2.5** — failure reports now persist to `Baseline/failures/{scenario}.log`, written whole on
  every failing run and deleted when the scenario passes. A journey row records *that* a scenario
  went 2/5, never *why*; until now the only legible diagnosis was the assertion message on a
  terminal, so a closed window meant a paid run with nothing left to learn from. That is exactly
  what happened once in this phase. The transcript is the expensive part of a run and writing it to
  disk costs nothing.
- **A2.5.1** — no change to the instrument, and that was the point: the phase moved the framework and
  nothing else, so its rows are comparable to A2.5's straight across. `monkeys` was skipped from proof
  (no context-scoped parameter, so the injection cannot fire), not from budget, which is a reading of
  the scenario rather than an edit to it.
- **A2.5, reverted** — eight turns across four context scenarios gained one shared `judgeNot`
  against the assistant explaining its own workings. The gap it closes is precise: non-revelation was
  asserted only by the forbidden-substring list, which catches the internal **nouns** (`context
  variable`, `GetContextVariable`, …) and nothing else, so a turn saying "let me see whether I
  already have your code… ah yes, I do" names none of them and passed. We were measuring the
  vocabulary and not the behaviour. Six of the eight turns had no natural-language assertion of any
  kind before this. **The gap is real and remains open; the proposition was reverted, and the
  scenarios are byte-identical to their A2.3 state.**

  It was reverted for two reasons, and both are worth more than the assertion was. The first is
  method: it entered a **blocking** group in the same measurement as a prose change, so every red
  after it was unreadable as a signal about the prose, because the ruler had moved. Prose and
  instrument are two axes and only one may move per measurement — a rule that was being applied to
  the prose axis alone, without noticing the other existed.

  The second is that the proposition could not have worked at any wording. It went through three,
  each rewritten after watching it misfire, and the last one fired on the very example it names as
  exempt: *"as they appear in my ledger"*, delivering invoices, judged as a claim about what the
  assistant retains. The axis was wrong, not the words. **The domain data genuinely is a ledger** —
  invoices really are records the system keeps — so "voice" and "machinery", the two sides of the
  intended distinction, use the same noun and no phrasing separates them. The axis that would work
  is *the customer's own data vs the assistant's own memory*: that Morgana can fetch your invoices
  is something the user is supposed to know; that she is checking whether she already holds your
  code is not. Rebuilding it belongs to A2.6, validated offline against the captured corpus in
  `failures/` before it is allowed to fail anything.

  It did earn its keep before being pulled. Two of its eight fires were genuine, and one of them is
  InventoryAgent telling a user her memory is *"fleeting as morning mist beyond this very
  conversation"* — the `agents.json` leak listed under **Still open**, found by the instrument
  rather than by reading. `context-closed-vocabulary-monkeys` was always out: its single turn is
  judge-free by an earlier decision recorded in the file itself, and its property is already
  asserted literally by `noContextAccess`.
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

- **The context group is green for the first time.** `context-cross-agent` closed at A2.5.1 — see
  that phase. It was the suite's longest-standing red and it was never a prose defect.
- **`context-cross-agent` turn 3 infers what it should measure.** It proves the agent did not ask and
  did call the domain tool, and the context read follows only by elimination (empty per-agent history,
  and the injected note carries no values). It should assert `contextReads: [Hit:userId]` outright.
  An instrument-only change, never to be bundled with a prose phase.
- **The parameter descriptions have never been read by a model, and now are.** A2.5.3 repaired the
  channel; the prose that travels down it was authored blind, across seven months, by people who could
  not see it land. Every one of them is now due a review it has never had — and the review question is
  not "is this well written" but **which rung of the ladder is this sentence standing on**. With all
  three rungs live for the first time, each statement is doing exactly one of three jobs: *first-stating*
  something nothing else says, *reinforcing* a rule stated higher up at the moment it is acted on, or
  merely *duplicating*. Only the third is waste, and the census has to be redone from scratch, because
  a sentence that was first-stating while the parameter channel was dead may be duplication now.
- **`ToolParameterRequestGuidance` is live again** after seven months inert — 98 characters on every
  request-scoped parameter of every tool, MCP tools included, since `MCPToolAdapter` scopes all remote
  parameters `request`. It was re-read before the repair shipped, as this journal required, and kept:
  read against the tool-level `ToolDescriptionContextGuidance` that enumerates the *context*-scoped
  names, it draws the same boundary from the other side and at the rung where the model is already
  binding that argument. That is the reinforcement reading; the duplication reading is available too,
  and it is the cheapest single thing to cut if the census says so.
- **MCP optional parameters are advertised as required.** Found in passing while probing A2.5.3.
  `MCPToolAdapter.ExtractParametersWithTypes` reads the remote server's `required` array faithfully into
  `Records.ToolParameter.Required`, but `CreateTypedDelegateWithNamedParameters` builds a `Func<>` whose
  parameters carry no default values, and the schema's `required` list is generated from the delegate,
  not from the definition. `ValidateToolDefinition` does not catch it: it checks required-in-definition
  against optional-in-method, never the converse. Every MCP parameter therefore reaches the model as
  mandatory — so the model supplies a value the server never asked for, and `ConvertValueForMCP`
  forwards it, coercing an absent one to `"0"`, `false` or `""`. The symptom belongs to the same family
  as A2.5.3's: an agent asking for, or inventing, what it should not.

  **The cheap repair is not available, and this was tested rather than assumed.** `DynamicMethod`
  cannot declare a default value: its `DefineParameter` returns `null` instead of a `ParameterBuilder`,
  so `SetConstant` cannot be called. Passing `ParameterAttributes.Optional` alone does make
  `IsOptional` true while `HasDefaultValue` stays false, and `AIFunctionFactory.Create` then throws
  `JsonException: The JSON value could not be converted to System.Double` while generating the schema —
  agent creation would fail outright. A correct repair therefore has three parts that must land
  together: the schema's `required` list (via `AIJsonSchemaCreateOptions.TransformOptions`), the
  argument binding for an omitted parameter (via `AIFunctionFactoryOptions.ConfigureParameterBinding`),
  and the executor's argument dictionary, which must omit rather than coerce. Native tools are unaffected
  — their C# defaults already agree with `agents.json`, and `ValidateToolDefinition` guards the pairing.
  Unrelated to prose, and outside the A2 arc.
- **The suite cannot see a change of this size.** `monkeys`, whose prompt the A2.5.2 cut could not
  touch, moved 4.9% on tokens. Five runs resolve pass rates, not payload arithmetic — so a phase whose
  claim is "this is cheaper" needs either a static measurement of the composed payload (as A2.1–A2.3
  used) or many more runs. Reading the token columns as evidence of a saving is a mistake this journal
  nearly made.
- **Four statements enforce one rule.** "Look a context-scoped value up before asking" is written in
  P0 `ContextHandling`, the framework `Instructions`, `ToolDescriptionContextGuidance` and
  `GetContextVariable`'s own description — `ToolParameterContextGuidance` was the fifth and was cut at
  A2.5.2. Three of the four existed to compensate for the fact A2.5.1 now supplies, so the redundancy
  is measurable rather than theoretical. Cut one at a time: the read half is duplicated, the **write**
  half (`SetContextVariable`, persist what you obtain) is carried by P0 and the tool-level template and
  must survive.
- **InventoryAgent tells the user what it cannot remember.** *"My memory is fleeting as morning mist
  beyond this very conversation"* — the surface form of a clause in `agents.json` about having
  nowhere to record preferences. The clause is load-bearing for `context-no-invented-writes`, so it
  cannot simply be cut: it has to be restated as a limit on **what she does** rather than on **what
  she keeps**. Deferred with its own measurement.
- **The prose is now tuned partly against these scenarios**, and that is a debt, not an achievement.
  The identity rule in BillingAgent and ContractAgent, and InventoryAgent's clause about having
  nowhere to record what a customer likes, were all written after watching a scenario fail. Each is
  defensible on its own terms — a real constraint, in the right layer — but a suite is only evidence
  while the prose is not written to it, so a coming phase should add coverage it has never seen
  rather than deepen what it already has. **A2.5 paid down the first half**: the identity rule was
  deleted from both agents once the framework carried the turn order, and the residual
  `context-cross-agent` failure is the honest invoice for it. The InventoryAgent clause stands.
- **`behaviour-conversation-closure` reached 5/5** at the close of A2.3, so the Family-A-list defect
  that stood open since A2.1 (a choice list emitted with no escape pair, trapping the user) was not
  observed again. It was never repaired deliberately, so treat it as unproven rather than fixed.

## Planned

Phases named but not yet run. Each is recorded here with the one thing that has to be settled before
it is worth spending a measurement on it — in both cases below, that thing is the same: the suite
cannot currently see the prose the phase would revise.

### `A2.6` — the four actor prompts

`morgana.json` carries five prompts and only one of them, `Morgana`, has ever been revised. `Guard`,
`Classifier`, `Presentation` and `ChannelAdapter` stand as first written. Each has at least one
defect visible by reading, before any measurement:

- **Guard** is asked for `violation` as "reason or null", with no guidance on voice, language or
  person — but that string is user-visible: `GuardAnswer` splices it ahead of a line in Morgana's
  voice, so the product emits an analytic fragment glued to a witch. Its Target also lists categories
  (violence, offensive content) with nothing separating a user *behaving* badly from a user *talking
  about* a subject, and unlike the LLM error path the classification itself fails closed. Adjacent
  and not a prose matter: the pre-LLM profanity scan is `message.Contains(term)`, so `stupid` matches
  `stupidity` and blocks before any model reads the sentence.
- **Classifier** is asked for a calibrated `confidence` on every turn that nothing reads —
  `LLMClassifierService` logs it, the supervisor tags a span with it, no code branches on it. Either
  it gates something (a low score is a clarifying turn, not a guess) or it stops being asked.
- **ChannelAdapter** spends a paragraph generalising to capability properties "whose names you have
  never seen before". `ChannelCapabilities` is a fixed record; that prose guards nothing, on the one
  prompt that runs per message on exactly the channels that cannot afford it.
- **Presentation** is the only place quick replies are emitted outside the Quick Reply Doctrine, and
  rightly so — the front door has no Family B. Nothing says so, so a later editor will "fix" it into
  compliance.

**What must be settled first: none of the four is measurable today.** `Nrt:EnableGuardrail` is
false, so the Guard never runs. Every scenario uses the `nrt` channel with `MaxMessageLength=null`,
so `MorganaChannelAdapter` short-circuits and the ChannelAdapter never runs at all. No scenario
asserts on the welcome message. The Classifier is covered only in reflection — if it misroutes, the
wrong agent answers. Revising these four before the suite can see them is writing blind, so the
phase opens on the instrument: a degraded-channel scenario on the Rune capability profile and a
guardrail-on scenario, or an explicit, recorded decision to review by reading only.

### `A2.7` — the summarization prompt

`Morgana:HistoryReducer:SummarizationPrompt` rewrites the history every later turn reasons over, so
its failures are the expensive kind: unrecoverable, because the next agent cannot ask a tool for a
value it was never handed. It already carries real scar tissue — the reference-data list held
outside the prose budget, an order id kept on one line with its seal word, each credential labelled
with the entity it belongs to. What it has never had is a measurement.

**What must be settled first: it does not fire.** Reduction triggers above
`SummarizationTargetCount + SummarizationThreshold` = 20 messages, and the longest scenario is five
turns. Judging this prompt needs a scenario built for it: establish a value early, cross the
reduction boundary, then require that value again on the far side.
