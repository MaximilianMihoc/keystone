---
name: reflect
description: Sweep the current session for lessons that were not written down when they happened, and prune the Lessons list in CLAUDE.md. Use when the user says "reflect", "what did we learn", "update the lessons", "remember this for next time", or at the end of a session where something went wrong.
---

Lessons are meant to be written the moment they happen (see `## Lessons` in `CLAUDE.md`). This
is the backstop: it catches what slipped through and keeps the list sharp.

## Sweep the session

Read back through the conversation for each trigger `CLAUDE.md` lists: a correction or rejected
approach, an assumption a check proved wrong, time lost to the environment, and information you
had to dig for that should have been one line away. For each one, check whether a lesson was
already written. If not, write it now.

Skip anything that only mattered to this task. A lesson earns a place only if a future session,
on a different task, would plausibly hit it again.

## Write it properly

Use the form **trigger → what to do instead → why**. If you cannot name a concrete trigger, it
is not a lesson yet.

Put it in the strongest place that would have caught it: a test or hook that fails, then a rule
in the `CLAUDE.md` section it belongs to, then a line under `## Lessons`. If `CLAUDE.md` already
says it and it was ignored anyway, the existing text is not landing. Reword or move it rather
than adding a duplicate.

## Prune

Look for lines under `## Lessons` that this session showed to be wrong or stale, and lines that
belong in a main section by now. Past about 30 lines, fold related lessons into their sections.
Removing or rewording existing text changes what every future session sees, so show that diff
and wait for approval. New lessons do not wait.

## Report

List each lesson written or proposed and where it went. If nothing was missed, say so and stop.
An empty sweep means the in-the-moment habit is working.
