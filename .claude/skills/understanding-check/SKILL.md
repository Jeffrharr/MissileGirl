---
name: understanding-check
description: Interview the engineer about an open pull request to confirm they understand the change and how it affects the codebase, then clear the `understanding-check` merge gate. Use right before merging a PR.
---

# Understanding Check

You are conducting a pre-merge **understanding interview** about a pull request. The goal
is to verify — through probing conversation — that the engineer genuinely understands the
change they are about to merge and its effect on the codebase. When satisfied, you clear a
required GitHub status check (`understanding-check`) that unblocks the merge.

This is private and local. Do not post anything to GitHub except the final status flip.

## 1. Identify the PR

- If the user passed a PR number, use it. Otherwise resolve the PR for the current branch:
  `gh pr view --json number,title,url,headRefOid,baseRefName,body`
- If there is no open PR, stop and tell the user to open one first — the gate keys on the
  PR's head commit, so there is nothing to clear without a PR.

## 2. Load the change

- Diff and file list: `gh pr diff <num>` and `gh pr diff <num> --name-only`.
- Read the PR title/body for the stated intent.
- Read surrounding code in the repo as needed to understand blast radius — don't reason
  from the diff alone.

## 3. Interview

Ask probing questions a few at a time, and **follow up wherever answers are vague or
hand-wavy**. Do not accept surface-level answers. Cover, adapted to the actual change:

- **What & why** — what the change does and the problem it solves.
- **Blast radius** — which parts of the codebase this touches or could affect: callers,
  shared state, public interfaces, migrations, config, performance.
- **Edge cases & failure modes** — what inputs or conditions could break it; what happens
  when it fails.
- **Testing** — what's covered, what isn't, and why that gap is acceptable.
- **Rollback / risk** — how to undo it and the worst case if it's wrong.

Push back on shallow answers ("it just works", "shouldn't affect anything"). Ask the
engineer to point to specifics in the diff or codebase. Your job is to surface gaps in
understanding, not to be agreeable.

## 4. Decide

Only when the engineer has demonstrated real understanding:

- Give a short summary of what you heard.
- Ask for explicit confirmation: **"Ready to mark this PR as understood and unblock merge? (yes/no)"**
- If they cannot answer key questions, do **not** clear the gate. Tell them what's still
  unclear and offer to keep going.

## 5. Clear the gate

On an explicit "yes", flip the check to success via the vendored writer:

```
.understanding/set-status.sh success --pr <num>
```

- If `.understanding/set-status.sh` is missing, the gate isn't installed in this repo —
  tell the user to run `install-into.sh` from the Understanding Gate toolkit.
- After flipping, confirm the `understanding-check` status is green on the PR.

**Never set `success` without a real interview and an explicit confirmation.** Pushing new
commits re-arms the gate to `pending`, so a later code change correctly requires re-running
this check.
