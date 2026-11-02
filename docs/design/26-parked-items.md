# Parked: fun-systems work (doc 26)

Status: PARKED · Recorded 2026-11-02 · Source: coordinator direction on owner priorities.

## Why parked
Fun systems are secondary to the north star: Windows and Android Bazaar working for GC, Kanban and Constellation. Work on doc 26 resumes after those are in use.

## Done
- Psych Ward state machine (26 WP2): pure logic, tests.
- Vent drafts with the publish gate (26 WP3): pure logic, tests.
- I'm Feeling Lucky controller (26 WP7): pure logic, tests.

Merged as PR #159 (squash commit 96130cd). Settings binding, stores and host wiring are still open (see `TODO(component)` markers in `src/Bazaar.Server.Components.Fun`).

## Parked
- Replay projector and `fork_link` (26 WP5). Needs migrations and Sonnet review.
- Mirror signals and profile (26 WP6). Server only.
- Playtest Tier A and Tier B (26 WP8).
- Sidewalk type defaults and wake rule (26 WP9).

## To resume
Start from the work packages in `docs/design/26-fun-systems.md`. Answer the owner questions in that doc's "Questions for the owner" section first. Check the migration registry before adding SQL.
