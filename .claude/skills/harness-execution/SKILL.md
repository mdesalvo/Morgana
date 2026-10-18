---
name: harness-execution
description: Runs PromptHarness on demand against the currently configured LLM tiers, over a user-chosen scope and run/pass threshold. Live LLM calls, real cost. Use when the user says "run the harness", "execute the prompt harness", "launch the harness".
---

# HarnessExecution

Runs `PromptHarness/PromptHarness.csproj` against the tiers configured in User Secrets. Every
billed run needs two answers first, scope and threshold, never assumed from a previous run.
`PromptHarness/CLAUDE.md` describes the groups and what a failure in each means: read it first.

## Procedure

1. **Confirm the tiers.** The harness shares `Morgana.Web`'s User Secrets store: read it through
   the tool, which knows where the platform keeps it, filtering to the keys below so that no key or
   endpoint is printed:
   ```
   dotnet user-secrets list --project Morgana/Morgana.Web | grep -E "Tiers:[^:]+:(Provider|Options:ModelId) |ActorSystem:Tier |HarnessDirectory "
   ```
   Tell the user the `Provider` and
   `Options:ModelId` of `Economy`, `Efficiency` and `Performance`, plus `Morgana:ActorSystem:Tier`
   (store or `Morgana.Web/appsettings.json`). Read `Harness:HarnessDirectory` in the same pass: where
   the store has none, the default is `PromptHarness/Harness/`. That directory is **the one place a
   run is read from**, by the user and by you alike: rows, failure logs and the console output. Never
   the scratchpad, or the two of you are reading different evidence.

2. **Ask the scope**, in the message text and not with `AskUserQuestion`. Offer what
   `PromptHarness/Tests/` actually holds, by family as `PromptHarness/CLAUDE.md` groups it
   (deterministic, blocking, behavioural, boot-flagged). A filter naming a class that no longer exists
   runs zero tests and exits 0. A user who said "everything" has answered.

3. **Ask the threshold**: 5/4 (default), 3/2 (cheap diagnostic), 5/5 (strict) or a custom N/M. It is
   passed as `Harness__DefaultRuns=N Harness__DefaultMinPasses=M` and is a fallback only: a scenario
   YAML declaring its own `runs`/`minPasses` keeps it. Say so before running.

4. **Run `HarnessSmokeTests` first**, with no overrides. If it fails, stop: a broken observer reads
   exactly like a prompt regression. A `DirectoryNotFoundException` on a stale `wwwroot` path is a
   build cache: `dotnet clean` then `dotnet build` the project.

5. **Run each group as its own invocation, one at a time**, never combining filters (the boot knobs
   below must not leak into other groups) and never in parallel (one shared `bin`/`obj`):
   ```
   <knobs> Harness__DefaultRuns=N Harness__DefaultMinPasses=M dotnet test PromptHarness/PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.<Class>" > <HarnessDirectory>/console/<Class>.log 2>&1
   ```
   | Class | Knobs |
   |---|---|
   | `GuardTests` · `ConversationPersistenceTests` | `Harness__EnableGuardrail=true` |
   | `SummarizationTests` | `Harness__SummarizationThreshold=4 Harness__SummarizationTargetCount=4` |
   | `DustTests` | `Harness__DustBudgetPerConversation=1.5` |
   | `RateLimitTests` | `Harness__RateLimitPerMinute=3` |
   | `FederationTests` | `Harness__FederatedPeer=true` (replaces the whole domain) |

   A single scenario is filtered with `--filter "DisplayName~<id>"`, but xUnit cuts the id to its
   first 50 characters in the display name: filter on those 50, or a longer id matches nothing.
   Create `<HarnessDirectory>/console/` first. Deterministic groups need no threshold. Always redirect to a file, never pipe through `tail`: it
   truncates the saved output too. **Read the test count in the summary**: zero tests is not green.

6. **Watch the run.** Launch it with `run_in_background: true`, note the time just before, then arm a
   `Monitor` (`timeout_ms: 1800000`, re-armed while the run lives) that only wakes you:
   ```
   while pgrep -f "[d]otnet test PromptHarness" >/dev/null; do sleep 120; echo tick; done; echo done
   ```
   It watches `dotnet test`, alive from the first second: the test host appears only after the build.
   Each scenario writes `{HarnessDirectory}/{id}.md` the moment it ends; a run that failed inside it
   also writes `failures/{id}.log`. At every tick classify each scenario landed since the launch:
   - **GREEN**: every run passed, no fresh failure log.
   - **ORANGE**: the log's `verdict:` says "passed at threshold". Reported, not acted on.
   - **RED**: the log's `verdict:` says "FAILED". KO, blocking.

   Report one line per tick: how many landed, which are new, each with its colour. On a RED kill the
   run, stop the monitor, read `failures/{id}.log` and `failures/{id}.model.log` and give the diagnosis
   from that evidence. Never relaunch or move on without the user's word; what had not landed is
   reported as not run. On `done` go to step 7.

   **Killing a run, for a RED or on request, is `pkill -KILL -f`** on `[v]stest.console.dll.*PromptHarness`,
   `[P]romptHarness/bin/.*/PromptHarness` and `[d]otnet test PromptHarness`. Never SIGTERM, never
   `TaskStop` alone: a terminated run records every pending scenario as 0/5, overwriting its last real
   row. The bracket keeps a pattern from matching the shell that carries it. A row with 0.0 LLM calls
   whose failure log says "run aborted: … Connection refused" is such an artefact, never a measure:
   report it as not run.

7. **Report a table**, one row per scenario actually run: `Scenario | Group | Result`, with one line
   under each failure naming the assertion or judge proposition. Judgment goes in prose below.

## Rules

- A judge failure is not automatically a prompt defect: check the proposition is well calibrated
  first. The judge runs on `Efficiency` and reads a proposition as an act, never as a wording.
- Say whether a failure happened at a loosened threshold or at the scenario's own.
- Leave `Harness:Phase` alone unless the user raises it.
- Never edit `morgana.json`, `agents.json` or a scenario YAML while running this skill; fixing is
  separate work the user drives. Never commit.
