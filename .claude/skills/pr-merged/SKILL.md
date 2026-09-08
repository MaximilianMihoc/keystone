---
name: pr-merged
description: Catch the local clone up after a pull request has been merged on GitHub - switch to main, pull it, and delete the branch that just landed. Use this whenever the user signals a PR has merged and wants their working copy tidied: "pr merged", "merged", "it's merged", "PR is in", "landed", or a request to move to main and pull after a merge. Trigger it even when they name only one of the steps, since the other two are almost always wanted too.
allowed-tools: Bash(git:*)
---

Bring the working copy back to a clean `main` after a pull request has merged.

The whole job is three commands, but the order matters and two of the steps have a trap in
them, which is what this is for.

## Before anything

Note the current branch: `git branch --show-current`. Do this **first** — once you are on
`main` the name is gone, and it is what step 3 needs.

Then check `git status --short`. Uncommitted changes would be dragged onto `main` by the
switch, or block it outright. If the tree is dirty, stop and ask what to do with the changes
rather than guessing — the user may have work in progress that has nothing to do with the PR.

If they are already on `main`, there is no branch to delete. Pull, then look for leftover
merged branches (below) and offer to clean those up instead.

## The three steps

1. `git switch main`
2. `git pull`
3. `git branch -d <the branch noted above>`

Use `-d`, never `-D`. Lowercase `-d` refuses to delete a branch whose commits are not on
`main`, so it is the check that the merge really did land — the safety net, not an obstacle.
If it refuses, stop and report: it means what the user thinks merged did not, or merged
somewhere else, and forcing past that destroys the only copy of their work.

## Do not push a remote delete

Reaching for `git push origin --delete <branch>` is the obvious next move and it will fail:

```
error: unable to delete '<branch>': remote ref does not exist
```

GitHub deletes the head branch itself when the PR merges, so by the time anyone asks, the
remote branch is already gone. What is left is a stale remote-tracking ref in the local clone.
Clear it with `git fetch --prune`, which also confirms what was removed.

## Reporting back

Say which commit `main` landed on and what the merge actually brought in — `git pull` prints
the diffstat, and it is worth repeating, because a fast-forward that pulls in nothing like the
expected files is the first sign the wrong thing merged.

Then run `git branch` and mention anything still lying around. Merged branches accumulate
quietly, and `git branch --merged main` tells you which are safe to remove. Offer, do not
assume — deleting branches is the user's call.
