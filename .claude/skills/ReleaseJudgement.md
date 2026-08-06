# ReleaseJudgement

Produces an objective, third-party assessment of a just-concluded development iteration: what the
team did, in which directions and modalities, and for what reasons — written as an outside evaluator
who only has access to commits, CHANGELOG and shipped artifacts, never to the internal process that
produced them.

## Trigger

Activated when the user says things like:
- "act as an external judge on this release"
- "judge the trajectory of this iteration"
- "what did the devs do this iteration, and why"
- "give me a third-party assessment of this release"
- Any request for a third-party, arms-length evaluation of a finished (or finishing) dev cycle

## Procedure

1. **Establish the boundary of the iteration.** Default to the version at the top of `CHANGELOG.md`
   (typically marked `UNDER DEVELOPMENT` or the most recently closed one) unless the user names a
   different version or commit range. Confirm the boundary back to the user in one line before
   proceeding if it's not obvious which iteration is meant — this is the one thing worth a quick
   check, since judging the wrong range wastes the whole exercise.

2. **Gather evidence, don't infer from memory:**
   - Read the relevant `CHANGELOG.md` section in full (Added / Changed / Fixed / Future Enablement,
     and the "Major Feature" narrative blurbs if present).
   - Run `git log --oneline <start>^..<end>` for the iteration's commit range to see the full shape,
     including any tail commits the CHANGELOG entry doesn't individually mention (doc polish, CI
     bumps, cleanup) — these matter for the "modalities" half of the judgement, not just the
     headline features.
   - If the current conversation *is* the iteration (or covers a meaningful slice of it), draw on
     what was actually observed happening — findings, dead ends, reversed decisions, criteria
     applied — not just the final commit messages, which flatten all of that away.
   - Read any diffs whose content isn't obvious from the commit message or CHANGELOG line alone,
     when a claim in the judgement needs to be grounded in something concrete (a file path, a
     specific mechanism, a specific finding).

3. **Write as an outside judge, not as a participant.** Never use "we" or "abbiamo fatto" — the
   judge did not do the work, they are evaluating it after the fact from its traces. Never simply
   restate the CHANGELOG in different words: a changelog answers "what shipped," this exercise
   answers "what was the team actually solving, and why did they choose to solve it this way."

4. **Organize by theme, not chronology and not per-commit.** Group related commits/changes under
   the underlying problem or design thread they belong to (e.g. "moving control signals out of free
   text," "making a guarantee provider-independent," "instrument hygiene"). A theme should:
   - be grounded in concrete evidence (file paths, mechanism names, specific findings) — never a
     vague claim like "the team improved reliability" with nothing under it
   - explain the *why*, not just the *what* — what problem was this solving, what would have gone
     wrong without it
   - connect to the project's own stated design philosophy where relevant (e.g. Morgana's
     `CLAUDE.md` doctrine that a defect shared by two agents belongs to a policy gap, never a
     per-agent patch) — a judge who knows the codebase's own stated values can check the work
     against them, not just against generic engineering taste

5. **Close with a one-paragraph verdict**: a single thesis sentence that the whole judgement
   argues toward, stated plainly, followed by why it matters going forward. Avoid marketing language
   ("game-changing," "robust," "seamless") — a judge's job is calibrated assessment, not promotion.

6. **Default output is prose in the conversation**, in whichever language the user asked in. If the
   user asks for it as a **deliverable to attach somewhere** (a GitHub release, a doc), producing a
   **self-contained HTML file** is the default shape unless they specify otherwise:
   - **Always written in English**, regardless of the conversation's language — this deliverable
     travels outside the conversation (a release page, an attachment) and outlives it, so it follows
     the language of the codebase and its release history, not the language of the chat that
     requested it. Only the in-conversation prose (when no file is requested) follows the user's
     own language.
   - Write it to the session's scratchpad directory, never into the repository, unless the user
     explicitly asks for it to be committed
   - Self-contained (inline CSS, no external assets/fonts/scripts) since it travels as a standalone
     attachment
   - Restrained, editorial visual register (a masthead, a pull-quote thesis box, numbered theme
     sections, a closing verdict block) — it is a piece of writing, not a dashboard; resist adding
     charts, badges or decoration that doesn't carry information
   - Support both light and dark rendering (`prefers-color-scheme`) since it may be viewed embedded
     in a GitHub release page, which respects the viewer's OS theme
   - Tell the user the file path when done

## Notes

- This is a judgement exercise, not a status report — a good output should be able to surprise the
  person who did the work by naming a pattern across changes they hadn't stated explicitly
  themselves, the same way a good code review finds the shape of a bug the author didn't see.
- Do not pull punches to be polite, and do not manufacture criticism to look balanced. If the
  iteration was solid, the verdict says so plainly and explains what specifically earned that; if
  something is thin or undocumented, the judgement names it rather than working around it.
- Never fabricate evidence. If the commit range or CHANGELOG don't support a claim, drop the claim —
  a judgement is only as credible as what it can point to.
- Committing the skill's own output (an HTML deliverable, or edits made while gathering evidence) is
  never automatic — this skill produces an assessment, it does not modify the codebase.
