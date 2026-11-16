# Astro's Handbook Loading Fix — safety preview

Client-side fork of astroval0's LGPL-2.1 mod. Target: **Vintage Story 1.22.7**.
This is an experimental build, not an in-game-tested release.

## Changes

- Index synchronous guides immediately, without waiting for item generation.
- Stream only pages accepted by vanilla's completed-page producer, after constructors,
  object initializers and meal-recipe caches. No base-constructor interception.
- Capture reload ownership when queuing work; serialize producers per dialog.
- Reject callbacks after reload, dialog disposal or shutdown.
- Keep completed pages and log producer exceptions instead of abandoning finalization.
- Always rebuild final page-code indices, preserving vanilla's last-page-wins ordering.
- Coalesce GUI work; at most 256 streamed pages per callback, with delayed continuation.
- Empty search field shows loading/partial-result status. This is a placeholder,
  **not a persistent banner**: typing hides it. Restore normal placeholder on completion.
- Preserve content scroll offset across incremental overview refreshes and refresh
  synchronous guides immediately when reloading an already-open handbook.
- Contain invalid/throwing page codes on the GUI thread, preserve the committed
  lookup on failed final indexing, and terminate loading with a logged error.
- Preserve the original empty-regex optimization.

Guides can still link to item pages that have not loaded. Those links become usable
when their targets arrive. This does not accelerate expensive item descriptions,
pre-dialog handbook-stack setup or other world-startup work.

## Compatibility and limits

Only vanilla `ModSystemSurvivalHandbook.onCreatePagesAsync` has a verified streaming
boundary. Other/custom producers are published when they return their complete list;
constructing arbitrary modded pages is never treated as proof they are ready.

Worker-scheduling and producer transpilers check the expected 1.22.7 instruction
sites and reject mismatched layouts. Mods that replace these methods, or patch
`LoadPages_Async` expecting it to run, require a compatibility check. Do not stack
with the original mod or other handbook loading optimizers without testing.

Headless tests apply Harmony patches to actual 1.22.7 assemblies and use fake API
services/synthetic pages to exercise loader scheduling and indexing. They do not
verify rendering, native GUI resources, a real world's timing or a full modpack.

## Build and test

Requires .NET 10 SDK and a legally obtained Vintage Story 1.22.7 installation.
Game assemblies are not included.

```sh
dotnet run --project tests/RegressionTests.csproj -p:VintageStoryDir=/path/to/Vintagestory -p:InstallMod=false
dotnet build -c Release -p:VintageStoryDir=/path/to/Vintagestory -p:InstallMod=false
```

Release ZIP is written into `bin/`. Building no longer installs automatically.
To explicitly install during a build, pass `-p:InstallMod=true` and set
`-p:ModInstallDir=/your/client/Mods/AstrosHandbookLoadingFix` if needed.

## Manual acceptance checks before regular use

1. Back up client mod list. Replace original mod; do not load both builds.
2. Join the same world/modpack. Open Handbook immediately. Check guide navigation.
3. Check progressive item arrival and placeholder status, including active searches.
4. Open a meal recipe during loading; check no missing recipe-cache errors.
5. Scroll, search and navigate back while batches arrive. Check position and query.
6. Change hotkeys repeatedly during loading. Check no duplicates or stale pages.
7. Leave/rejoin during loading. Check logs for post-disposal errors.
8. Verify complete final catalog and guide/item links against the original mod.

## License

LGPL 2.1; see `LICENSE`. Original author: astroval0. Modified source accompanies
this preview. No endorsement by the original author is implied.
