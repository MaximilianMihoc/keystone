---
description: Branch, commit, push and open a pull request for the current changes
argument-hint: [optional branch name or a note on what this change is]
allowed-tools: Bash(git:*), Bash(gh:*), Read, Grep, Glob
---

Take the current working-tree changes all the way to an open pull request.

Context hint from the user (may be empty): $ARGUMENTS

Work through this in order, and stop to report if any step is not safe to take.

1. **Look before acting.** Run `git status` and `git diff` (plus `git diff --cached` if
   anything is staged). Understand what actually changed — the commit message and PR body
   must describe the real change, not a guess.

2. **Get onto a branch.** If `git branch --show-current` says `main`, create one with
   `git switch -c <name>`. Derive `<name>` from what changed, using the repo's existing
   prefixes: `feat/`, `fix/`, `chore/`, `docs/`. Prefer the user's hint above if they gave
   one. If already on a non-main branch, stay on it.

3. **Commit.** Stage deliberately — name the paths rather than reaching for `git add -A`,
   unless everything genuinely belongs together. Write the message in the style the repo
   already uses: a short imperative subject, a blank line, then prose explaining why the
   change is right, not a list of what the diff shows. End with the attribution trailers
   this session is configured to use.

4. **Push.** `git push -u origin <branch>`. The pre-push hook refuses `main`, which is
   expected and correct — never work around it with `--no-verify`.

5. **Open the PR.** Use `gh pr create`, filling in `.github/PULL_REQUEST_TEMPLATE.md`
   properly: what changed, why, and how it was verified. Be honest in the verification
   section — if you did not run the tests, say so rather than ticking the box. Add the
   generated-with footer this session is configured to use.

6. **Report back** with the PR URL, and say plainly what you did not verify so the user
   knows what still needs checking.

Do not merge the pull request. That is the user's call.
