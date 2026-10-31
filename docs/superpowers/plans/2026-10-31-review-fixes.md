# Review issues 24–31 implementation plan

> **For agentic workers:** Use superpowers:executing-plans inline. The user authorized implementation of the eight issue proposals; preserve existing gameplay and save compatibility.

**Goal:** Make economic effects resumable, reconcile real cargo before site closure, recover failed pods, bound consumed-loot history, preserve cargo damage, and preview early returns.
**Architecture:** Keep resumable physical output in a Scribe-owned ThingOwner batch per effect. Reconcile ownership/physical state rather than retrying whole mutations. Put preview and retention rules in Core, and test game boundaries through the game test harness.
**Tech Stack:** C# / .NET 10, RimWorld 1.7, xUnit.
**Spec:** GitHub issues #24–#31 and docs/superpowers/specs/2026-10-23-rental-warehouse-design.md.

## Global constraints
- No game binaries in git or artifacts. Preserve existing schema defaults.
- Existing active contracts and deadlines must remain unchanged.
- No merges or releases as part of this change. Open a non-draft PR after verification.

## Review focus
- Partial placement, transfer, and retry across save/load must conserve quantities.
- Unclaimed, pending, or unopened cargo must survive cleanup.
- Pending pod requests must not be mistaken for failed ones.
- New payment reservations must not allow new contracts while debt remains.
- Preview must not mutate the live ledger or enqueue effects.

### Task 1: Core lifecycle and preview (#28–#30)
- [x] Add failing tests for exhausted pod request recovery, consumed site cargo retention, and early-return quote immutability.
- [x] Run targeted tests and observe failures.
- [x] Implement recovery in ReturnService, consumed-loot retention in LifecycleService, and ContractEngine.PreviewEarlyReturn.
- [x] Run Core suite and commit.

### Task 2: Physical economic effects (#24–#26)
- [x] Add runnable tests of the shared resumable transfer algorithm with partial/throwing handoffs.
- [x] Add saved output batch IThingHolder, route silver/cargo outputs through it, and keep unpaid debt until full reservation.
- [x] Reuse unplaced delivery crates and confirm only physical handoff counts as delivery.
- [x] Add in-game failure/retry cases; build adapter and game tests, run Core tests.

### Task 3: Site and UI integration (#27, #30, #31)
- [x] Add game cases for ownership before closure and damaged-cargo writeback.
- [x] Synchronize ownership before close; write back HP before disposing site crate.
- [x] Add translated early-return quotes and disable duplicate debt actions while pending.
- [x] Run full verification, inspect whole diff, commit and open PR linked to all eight issues.

Execution note: runtime game cases were built but require manual in-game execution; this is recorded in docs/testing/review-24-31.md. Final PR publication follows verification.
