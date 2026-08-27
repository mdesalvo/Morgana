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
   without printing the ApiKey/Endpoint values, and tell the user which provider/tier models
   (`Tiers.Efficiency.Options.ModelId` / `Tiers.Performance.Options.ModelId`) this run will hit. This
   is the harness's own design (`PromptHarness/README.md`): it never has its own `Morgana:` config,
   it inherits the host's.

2. **Ask the target scope** with `AskUserQuestion`, multi-select, one checkbox per test class:
   - Context (`ContextHandlingTests` — the blocking group: context cycle, closed vocabulary, cross-agent)
   - Behavior (`BehaviourTests` — turn continuation, closure, rich cards)
   - Actors (`ActorTests` — classifier, channel adapter, presentation)
   - Guard (`GuardTests` — requires `Harness__EnableGuardrail=true`, off by default)
   - Summarizer (`SummarizationTests` — requires `Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4`, unset by default)

   Offer "everything" as an implicit fifth option by allowing all five checkboxes selected at once.
   Do not default to any pre-checked selection — the user picks the perimeter explicitly every time.

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
   own extra flags, one `dotnet test` invocation per group (never combine filters — Guard and
   Summarizer need boot-time flags the other groups must NOT carry):
   ```
   # Context
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ContextHandlingTests"

   # Behavior
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~BehaviourTests"

   # Actors
   Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~ActorTests"

   # Guard
   Harness__EnableGuardrail=true Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~GuardTests"

   # Summarizer
   Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4 Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~SummarizationTests"
   ```

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
   - The user running the suite may have their own `Harness__HarnessDirectory` override pointing
     somewhere else entirely (a personal results folder, a shared drive) — confirm with them where
     they're actually watching before assuming the in-repo default is it; do not assume two different
     configured paths are the same run's output.
   - If neither location has fresh files after a run that should have produced them, do not silently
     shrug — a scenario ID and phase both funnel into the file name, so an empty/missing folder is
     itself worth flagging rather than only relying on the `dotnet test` console summary.

8. **Report a results table**: one row per scenario actually exercised (not per test class — a
   `[Theory]` class covers several scenario IDs), columns `Scenario | Group | Result`, plus a short
   note under any row that failed (which assertion or judge proposition, one line). Do not editorialize
   pass/fail severity in the table itself — keep judgment calls in prose below it.

## Notes

- Every run is billed — live LLM calls, no mocking. Never run without both questions (scope,
  threshold) answered first, and never assume a repeat of a previous scope/threshold.
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
