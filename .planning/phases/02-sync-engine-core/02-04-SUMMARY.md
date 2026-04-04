---
phase: 02-sync-engine-core
plan: "04"
subsystem: worker/sync-engine
tags: [throttle, resilience, graph, singleton, tdd]
dependency_graph:
  requires: ["02-03"]
  provides: ["ThrottleCounter singleton", "GraphResilienceHandler onThrottle wiring", "SyncEngine throttle tracking"]
  affects: ["worker/Services/SyncEngine.cs", "worker/Program.cs", "worker/Graph/GraphResilienceHandler.cs"]
tech_stack:
  added: []
  patterns: ["Interlocked.Increment/Exchange for thread-safe counters", "Singleton-to-scoped counter bridge", "Factory delegate DI registration with captured dependencies"]
key_files:
  created:
    - worker/Services/ThrottleCounter.cs
    - tests/AFHSync.Tests.Unit/Sync/ThrottleCounterTests.cs
  modified:
    - worker/Program.cs
    - worker/Services/SyncEngine.cs
    - tests/AFHSync.Tests.Unit/Sync/SyncEngineTests.cs
decisions:
  - "ThrottleCounter is a concrete class (no interface) because it is a simple value holder with no behavior to mock — tests construct it directly"
  - "Singleton-scoped boundary solved with ThrottleCounter singleton that SyncEngine resets at run start and reads at finalize"
  - "Factory delegate for GraphResilienceHandler DI replaces bare AddSingleton to inject ThrottleCounter.Increment as onThrottle callback"
metrics:
  duration: "~3 minutes"
  completed: "2026-04-04"
  tasks: 2
  files_changed: 5
---

# Phase 02 Plan 04: ThrottleCounter and GraphResilienceHandler Wiring Summary

**One-liner:** ThrottleCounter singleton with Interlocked thread-safety bridges the GraphResilienceHandler singleton to SyncEngine's scoped throttle event tracking via Reset/Count pattern.

## What Was Built

Closed the Phase 02 verification gap where `SyncRun.ThrottleEvents` was always 0. The fix required solving a singleton-to-scoped boundary problem: GraphResilienceHandler is a singleton (one Polly pipeline shared across all Graph HTTP calls) while SyncEngine is scoped (one instance per sync run). A direct callback reference from singleton to scoped would have been unsafe.

**Solution architecture:**
1. `ThrottleCounter` (singleton) — thread-safe counter using `Interlocked.Increment` and `Interlocked.Exchange` for Reset, `Volatile.Read` for Count
2. `Program.cs` — registers `ThrottleCounter` as singleton, then wires it into `GraphResilienceHandler` via factory delegate: `onThrottle: _ => sp.GetRequiredService<ThrottleCounter>().Increment()`
3. `SyncEngine` — injects `ThrottleCounter`, calls `Reset()` at run start (after `ResetCache()`), reads `Count` at `FinalizeRunAsync`. Removed the always-zero local variable `totalThrottleEvents`

## Tasks Completed

| Task | Name | Commit | Files |
|------|------|--------|-------|
| 1 | Create ThrottleCounter singleton and wire DI | 66eb274 | worker/Services/ThrottleCounter.cs, worker/Program.cs, tests/.../ThrottleCounterTests.cs |
| 2 | Wire ThrottleCounter into SyncEngine and verify end-to-end | 27c315e | worker/Services/SyncEngine.cs, tests/.../SyncEngineTests.cs |

## Test Results

- **ThrottleCounterTests**: 5 tests pass (initial state, increment, reset, thread safety, concurrent reset)
- **SyncEngineTests**: 8 tests pass (6 existing + 2 new throttle tests)
- **Full suite**: 81 tests pass (0 regressions)
- **Worker build**: 0 errors, 0 warnings (warnings in SourceResolver/RunLogger are pre-existing, out of scope)

## Verification Gap Closure

| Check | Result |
|-------|--------|
| `totalThrottleEvents` variable removed from SyncEngine | PASS — grep returns 0 results |
| `throttleCounter.Reset()` and `throttleCounter.Count` in SyncEngine | PASS — found at lines 56 and 113 |
| `AddSingleton<ThrottleCounter>()` in Program.cs | PASS — found at line 42 |
| Bare `AddSingleton<GraphResilienceHandler>()` removed | PASS — grep returns 0 results |
| Factory delegate with `ThrottleCounter.Increment` as onThrottle | PASS — found at line 51 |

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None — ThrottleCounter is fully wired. SyncRun.ThrottleEvents will now reflect actual Graph retry counts during a real sync run.

## Self-Check: PASSED

Files exist:
- FOUND: worker/Services/ThrottleCounter.cs
- FOUND: tests/AFHSync.Tests.Unit/Sync/ThrottleCounterTests.cs

Commits exist:
- FOUND: 66eb274 (feat(02-04): add ThrottleCounter singleton and wire DI with onThrottle callback)
- FOUND: 27c315e (feat(02-04): wire ThrottleCounter into SyncEngine and add integration tests)
