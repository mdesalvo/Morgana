# The instrument you are writing for

PromptHarness is a live, black-box, on-demand suite that measures **prompt behaviour**, not code.
Nothing in it is mocked. Every turn of every scenario is a real call to a real model against a real
Morgana instance, booted in-process on an ephemeral port and driven over HTTP as a channel called
`harness` — full capabilities, no length budget, so nothing you assert is a degraded rendering.

It exists because a domain agent *is* its prose. There is no unit test for a paragraph, and the only
way to know a prompt revision did not break anything is to make the model do the thing and look.

A scenario is a YAML file. Its name is its `id`, and a test loads it by that name.

## What can be observed, and how

This is the part that decides which assertions are possible, and it is worth knowing exactly.

- **The message**: everything a user would see — the text, the quick replies, the rich card, whether
  the agent declared itself finished.
- **A span listener** on `morgana.agent`, which carries `agent.tools_invoked`. That is where
  `toolsCalled` comes from: tool **names only, in order, never arguments**. No assertion about what
  was passed to a tool is possible, because that data is deliberately not in the span.
- **A tee on the console**, matching the `HIT` / `MISS` / `SET variable '<name>'` lines `MorganaTool`
  emits. That is how context reads and writes are seen — again **names only, never values**.
- **An LLM judge** on the cheapest configured tier, for what structure cannot reach. The judge sees
  **only what a user would see**. It never sees the tool trace, the spans or the context log, so a
  proposition it cannot check from the reply alone is a proposition it will answer by guessing.

## Runs and thresholds

The system under test is a language model, so a single run proves nothing and a scenario that must
pass 5 times out of 5 is a different claim from one that must pass 3 out of 5. `runs` and
`minPasses` state that claim. Choose them for what the assertion means:

- Something the agent must **never** get wrong — a refusal, a confirmation before an irreversible
  action, a value never revealed — is contract. Set it high and equal: `runs: 3, minPasses: 3`.
- Something with **more than one lawful shape** — whether a list arrives as prose or as buttons — is
  a tendency. `runs: 5, minPasses: 3`.

Omit both and the harness runs it once, which is right only for a smoke check.

## What a good scenario does, and what a bad one does

A scenario is brittle when it asserts something true of today's mock rather than of the agent. The
mock will be replaced by the client's real system, and that is the one day the suite most needs to
still work. **Assert that a tool ran; never assert what it returned.**

A scenario is vacuous when it demands one of several lawful shapes. An agent that shows three
invoices may legitimately end with prose or with three buttons, and both obey the policy. Assert
what holds either way, and say in a comment which shapes you meant to allow — the comment is how the
next person knows the looseness was deliberate rather than lazy.

A `say:` line is what a real person types: lowercase, partial, two things at once, their words and
not the configuration's. A `say:` lifted from the intent description tests the classifier against
its own answer key.

Comment the reasoning, never the mechanics. Why a turn admits two shapes, why a threshold is five.
Not that `toolsCalled` lists the tools that were called.

## Scenarios from the suite that ships with Morgana

They follow this briefing, verbatim, as they run today. Read them as the idiom — the key order, the
comments that explain why a turn admits two shapes, the way a judge proposition is phrased so it can
be answered from the reply alone.

## The complete vocabulary

The keys follow. They are not a description of the schema — they are read off the harness's own
`ScenarioDefinition` type, which Alembic compiles in, so this list and the check your output is put
through afterwards are the same list. A key not on it is dropped in silence by the harness: the
scenario loads, runs, passes, and asserts nothing.

The base tools every agent receives, and which may be named in `toolsCalled`: `GetContextVariable`,
`SetContextVariable`, `SetTurnContinuation`, `SetQuickReplies`, `SetRichCard`.
