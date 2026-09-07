---
name: harness-execution
description: Runs PromptHarness on demand against the currently configured LLM provider, over a user-chosen scope and run/pass threshold. Live LLM calls, real cost. Use when the user says "run the harness", "execute the prompt harness", "launch the harness".
---

# HarnessExecution

Runs the PromptHarness (`PromptHarness/PromptHarness.csproj`) on demand against whichever LLM
provider is currently configured in User Secrets (`Morgana:LLM:Provider`), against a user-chosen
scope and a user-chosen global run/pass threshold. Live LLM calls, real cost — never runs without
the two questions below being answered first.

## Trigger

Activated when the user says things like:
- "run the harness"
- "execute the prompt harness"
- "launch the harness"
- Any request to run PromptHarness scenarios against the current provider

## Procedure

1. **Confirm the active provider** before anything else: read `Morgana:LLM:Provider` from the shared
   User Secrets store (`UserSecretsId 374228be-4f26-4382-a3ef-7500a0b829dd`, same as `Morgana.Web`)
   without printing the ApiKey/Endpoint values and tell the user which provider/tier models
   (`Tiers.Efficiency.Options.ModelId` / `Tiers.Performance.Options.ModelId`) this run will hit. This
   is the harness's own design (`PromptHarness/README.md`): it never has its own `Morgana:` config,
   it inherits the host's. The same file is where `Harness:HarnessDirectory` lives when the user has
   overridden it (see step 7), so read both in one pass. Note the store is written with a UTF-8 BOM:
   parse it as `utf-8-sig`, or a plain JSON read fails on the first character.

2. **Ask the target scope** with `AskUserQuestion`, multi-select. **Enumerate `PromptHarness/Tests/`
   first and offer what is actually there** — the class list below is a description of a moving
   directory, not a contract, and a `--filter` naming a class that no longer exists runs zero tests
   and exits 0: a green nobody asked for. Thirteen classes at the time of writing, in four families:

   *Deterministic — no model, no cost. Run them first: they are the cheapest way to learn the
   topology under test is sane before any billed turn.*
   - Startup validation (`StartupValidationTests` — an incoherent partner declaration must stop the boot)
   - Agent card (`AgentCardTests` — the published card and how far the gate behind it reaches)
   - Peer federation (`PeerFederationTests` — the outbound half: which cards this side accepts, what it signs, where a credential may go)

   *Blocking — a silent failure mode, which is why these two are the ones a revision stops on.*
   - Context (`ContextHandlingTests` — the context cycle, the closed vocabulary, cross-agent)
   - Consulting (`ConsultingTests` — a colleague reached on demand and the conversation left as it was found)

   *Behavioural — billed, judged.*
   - Behavior (`BehaviourTests` — turn continuation, closure, rich cards)
   - Actors (`ActorTests` — classifier, channel adapter, presentation)
   - Served consultation (`ServedConsultationTests` — this installation answering a partner: which conversation, what it cost, how many exchanges are admitted. Its refusals are decided before a desk is troubled and cost nothing; the rest runs a real model)

   *Boot-flagged — each needs a process-wide knob the other groups must NOT carry, so each is its own
   invocation. This is the whole reason filters are never combined.*
   - Guard (`GuardTests` — `Harness__EnableGuardrail=true`)
   - Summarizer (`SummarizationTests` — `Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4`)
   - Dust (`DustTests` — `Harness__DustBudgetPerConversation=15`; 3 and 8 both let one turn jump past 90% straight into exhaustion, which reads as "90% never appeared")
   - Federation (`FederationTests` — `Harness__FederatedPeer=true`, which stands a **second Morgana** up and **replaces the whole domain** of the instance under test with one toolless desk. Every other group would find its own desks missing, so this one never shares an invocation with anything)

   Plus `HarnessSmokeTests`, which is not a choice: step 4 runs it regardless.

   `AskUserQuestion` takes at most four options, so thirteen checkboxes do not fit: ask by **family**
   — the four above, multi-select — and let a user wanting a single class say so through "Other".
   A user who has already said "everything" has answered this question; do not ask it again.

   Do not default to any pre-checked selection — the user picks the perimeter explicitly every time.

   **Consulting takes no flag of its own** and the absence is worth stating because it is the natural
   thing to go looking for: `Morgana:AgentToAgent:Enabled` defaults to true and the host signs the
   traffic between its own agents under a key it coins at startup, so there is nothing for the fixture
   to mint or override. What this group does depend on instead is a **topology** — the scenarios name
   agents that must still declare `[ConsultsAgent]` of one another — so a failure here has a second
   thing it can mean and `Examples/Agents/*.cs` is the first place to look before the prose.

3. **Ask the global run/pass threshold** with `AskUserQuestion` (single-select with the following
   presets, "Other" covers any custom pair):
   - "5 runs / 4 pass (framework default)"
   - "3 runs / 2 pass (cheap diagnostic)"
   - "5 runs / 5 pass (strict, matches the blocking group's own floor)"
   - Other → ask for `N runs / M pass` as free text

   This threshold is passed as `Harness__DefaultRuns=N Harness__DefaultMinPasses=M` and is a
   **fallback only**: any scenario YAML under `PromptHarness/Scenarios/` that hardcodes its own
   `runs`/`minPasses` keeps its own value regardless of what is chosen here — the env var never
   overrides a scenario-level setting. Tell the user this before running, so a "3/2 selected" answer
   is not mistaken for "everything now runs 3/2."

4. **Run `HarnessSmokeTests` first, always**, before any selected group, with no env var overrides:
   ```
   dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~HarnessSmokeTests"
   ```
   If it fails, stop and report — do not spend a single live LLM call on the selected groups until
   the wiring itself is healthy (see `PromptHarness/README.md`: "a broken observer reads exactly like a
   prompt regression"). A `wwwroot`/static-web-assets `DirectoryNotFoundException` pointing at a
   stale absolute path is a build-cache issue, not a prompt issue: `dotnet clean
   PromptHarness/PromptHarness.csproj && dotnet build PromptHarness/PromptHarness.csproj` fixes it.

5. **Run each selected group** with `Harness__DefaultRuns=N Harness__DefaultMinPasses=M` plus its
   own extra flags, **one `dotnet test` invocation per group, strictly one at a time**. Filters are
   never combined, because four groups carry a boot-time knob the others must not see. And the
   invocations are never parallelised either, for a second reason: they share one csproj, so two
   concurrent runs fight over the same `bin`/`obj`.
   ```
   # Deterministic — no model, no cost
   dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~StartupValidationTests"
   dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~AgentCardTests"
   dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~PeerFederationTests"

   # Blocking
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"

   # Consulting — no extra flag: A2A is on by default and the host coins its own ring key
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ConsultingTests"

   # Behavioural
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~BehaviourTests"
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ActorTests"
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ServedConsultationTests"

   # Boot-flagged — one knob each, never together
   Harness__EnableGuardrail=true Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~GuardTests"
   Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~SummarizationTests"
   Harness__DustBudgetPerConversation=15 Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~DustTests"
   Harness__FederatedPeer=true Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~FederationTests"
   ```

   **Read the test count in every summary line, not only the pass/fail verdict.** A filter matching
   no class is not an error: the run exits 0 having executed nothing. A group reported green having
   run zero tests is the one failure mode of this procedure that looks exactly like success.

6. **Always redirect full output to a log file** in the scratchpad directory (`> file 2>&1`, never
   pipe through `tail` on the live command) — grep the file afterward for the summary line and any
   `[FAIL]`/`✗` detail. A truncated `tail` loses the per-run transcript needed to diagnose a failure
   without re-running (and re-paying for) the scenario. This is not a style preference: piping a
   backgrounded `dotnet test` through `tail -N` truncates the *saved* output file too (the pipeline's
   tail process is what actually writes it), so a multi-scenario run that fails early in the log can
   silently lose the one failure you needed, while later passing scenarios survive. Redirect first,
   read/grep second — never combine the two in one command.

   Launch the run with `run_in_background: true` (or accept the harness's own auto-backgrounding on
   timeout) and then **wait on it properly** — `TaskOutput` with `block: true`, or a `Monitor` — rather
   than babysitting it with manual `sleep` + `ls`/`wc -l` polling loops. The run is 5-10+ minutes of
   live LLM calls; do not spend turns re-checking a file that isn't done yet.

7. **Watch the configured output folder for live, per-scenario results instead of waiting for the
   whole run to finish.** `HarnessWriter` (journey row, `{HarnessDirectory}/{scenarioId}.md`) and
   `FailureLog` (failing-run transcript, `{HarnessDirectory}/failures/{scenarioId}.log`, deleted again
   once a scenario is clean) are both written the instant *that scenario's* `RunAsync` returns — not
   at the end of the whole `dotnet test` invocation. A `[Theory]` class with several `[InlineData]`
   scenario ids therefore drops files into that folder one at a time as the suite progresses, so it
   can be tailed/opened mid-run to see which scenarios have already landed and how, well before the
   process exits.
   - Default location: `Harness:HarnessDirectory` in `PromptHarness/appsettings.Harness.json`
     (currently the relative value `"Harness"`, resolved against the PromptHarness project root, i.e.
     `PromptHarness/Harness/` — gitignored, never expect it in `git status`).
   - The user running the suite may have their own `Harness:HarnessDirectory` override pointing
     somewhere else entirely (a personal results folder, a shared drive) — it lives in the same User
     Secrets store read in step 1, so look there rather than asking and only ask when the store has
     none. Do not assume two different configured paths are the same run's output.
   - If neither location has fresh files after a run that should have produced them, do not silently
     shrug — a scenario ID and phase both funnel into the file name, so an empty/missing folder is
     itself worth flagging rather than only relying on the `dotnet test` console summary.

8. **Leave `Harness:Phase` alone unless the user raises it.** It was the row key of the harness's
   own development phases and re-running a phase replaces its row rather than appending one. It no
   longer tracks anything the user asks about, so do not invent a new phase name for a run and do not
   edit `appsettings.Harness.json` to bump one.

9. **Report a results table**: one row per scenario actually exercised (not per test class — a
   `[Theory]` class covers several scenario IDs), columns `Scenario | Group | Result`, plus a short
   note under any row that failed (which assertion or judge proposition, one line). Do not editorialize
   pass/fail severity in the table itself — keep judgment calls in prose below it.

## Notes

- Every run is billed — live LLM calls, no mocking. Never run without both questions (scope,
  threshold) answered first and never assume a repeat of a previous scope/threshold.
- The judge (`LLMJudge`) always runs on the same provider under test, on its cheapest tier, with a
  deliberately strict system prompt ("do not be charitable"). A judge-proposition failure is not
  automatically a prompt defect — check whether the proposition itself is well-calibrated before
  concluding the agent is wrong (see the `behaviour-conversation-closure` / `context-cross-agent`
  precedent: the fix was reformulating the judge proposition to judge function over literal wording,
  not touching the agent's persona).
- A scenario failing at a loosened threshold (e.g. 3/2) that would have passed at 5/4 is not the same
  finding as a scenario failing at its own hardcoded blocking threshold (5/5) — say which one occurred.
- Never edit `morgana.json`, `agents.json`, or any scenario YAML as part of *running* this skill.
  Diagnosing and fixing a discovered defect is separate follow-up work the user drives turn by turn,
  the same way the AzureOpenAI diagnostic session that produced this skill did.
- Committing any resulting change is the user's call, never automatic.
