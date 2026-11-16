# Review: Astro's Handbook Loading Fix

Original reviewed commit: `478172779f37c048ab994c0a5b975f79bd9eec6c`.
Game reference source: `anegostudios/vssurvivalmod` commit
`849fa8cad9e392368566efc7474e73db6404a145` (1.22.7).
Production patches are smoke-tested against real 1.22.7.0 assemblies.

## Original defects and changes

1. **Reload ownership captured too late.** Two workers queued by different reloads
   can start afterward and both read the latest generation. Capture generation
   at scheduling time, skip stale workers, serialize producers and cancel stale
   vanilla producers at their next accepted-page boundary.
2. **Base constructor is not a completion boundary.** Nested modded constructors
   can cause an unfinished outer object to be published. Replace the global base
   constructor postfix with explicit accepted-page boundaries in the known vanilla
   producer. Unrecognized/custom producers retain safe full-list publication.
3. **Producer exceptions skip completion.** Ensure GUI-side completion is queued
   from `finally`, log the failure and preserve accepted complete pages.
4. **Final index can keep obsolete keys.** Reindex every final ordered page,
   including custom guides. Preserve last-page-wins collisions even when page
   keys/order change after initial publication.
5. **Disposed dialogs still accept callbacks.** Invalidate generation on dialog
   and mod disposal, check shutdown and generation before publishing.
6. **Overlapping producers mutate shared caches.** Serialize per-dialog producers;
   coalesce obsolete queued generations rather than generating every old request.

## UX changes

- Synchronous guides are indexed and searchable as soon as `loadEntries` returns,
  while background item generation continues.
- A loading/incomplete-results placeholder appears in the empty search field.
  Typing hides it; this is not a persistent loading banner.
- Streamed batches are capped at 256 pages, with one pending GUI callback and
  delayed continuations to avoid draining a full backlog in one frame.
- Builds no longer silently install the mod. Installation requires explicit opt-in.

## Additional issues caught during implementation review

- Overview scrollbar height updates changed content offset between batches. Capture
  content offset, filter without recomposing/resetting search, then restore/clamp
  the offset and trigger the scrollbar callback.
- An already-open overview stayed empty after reload until item work completed its
  first batch. Refresh guides synchronously after clearing the loading gate.
- Throwing/null page-code getters escaped onto the GUI thread. Validate before
  committing each streamed page; build final indices transactionally before
  replacing the live index/list. Contain callback errors, terminate status and
  abandon further publication for the failed loader. Preserve committed pages.

## Verification boundaries

Automated tests exercise synthetic page producers through real Harmony-patched
1.22.7 assemblies and fake client event/logger services. They cover guide indexing,
queued reload ownership, stale callback rejection, constructor-only page exclusion,
producer errors, changed keys/collision ordering, fallback publication, batching,
disposal, cooperative cancellation and producer serialization.

No actual world was opened. Rendering, full-modpack compatibility, search/scroll
behavior during real GUI refreshes and wall-clock improvement remain manual checks.
Do not interpret a successful build as proof of those outcomes.

The original defects are source-confirmed with deterministic triggering schedules;
this review does not assert each one occurred in the user's world.
