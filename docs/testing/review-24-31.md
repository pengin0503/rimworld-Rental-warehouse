# Issue #24–#31 verification

Base: `ec62fd29e38a6c2ca02d33b26ceeb52cd3b8802d` (RimWorld 1.7).

## Implementation

| Issue | Change | Verification |
|---|---|---|
| #24 | Scribe-owned physical output batches, saved silver reservations, pending debt until commitment; refund and unlock on lost map/creditor | Core payment/retry tests; shared transfer tests; existing game settlement cases compile |
| #25 | Stage each manifest once; keep physical remainder after rejected placement/partial transfer/exception | TransferRecovery tests; Review boundary game cases |
| #26 | Reuse and save an unplaced registered crate; require actual map/pod handoff | UnplacedRegisteredCrateMustActuallyBeDelivered game case |
| #27 | Synchronize player ownership inside CloseIfDue before deleting cargo records | SiteClosureSynchronizesPlayerHeldCratesFirst game case |
| #28 | Recover missing pod IDs only when no creation effect is pending; retain original deadline | MissingPodRecoversAfterAbandonment_ButNotWhilePending_AndKeepsDeadline |
| #29 | Remove consumed site-cargo records and completed open effects; preserve unopened/pending goods | ConsumedSiteCargoAndOpenEffectArePruned_EvenAfterSiteWasPruned / CleanupKeepsUnopenedAndPendingSiteCargo |
| #30 | Clone-based immediate-return quote: claimed advance, fee, credit, debt, actual trust change, completion bonus | EarlyReturnPreviewShowsPrepaymentDebtAndTrust_WithoutMutatingLiveState |
| #31 | Write actual crate HP before discarding the site map | SiteWritebackPreservesDamagedCrateHitPoints game case |

Core baseline: 333 tests passed. With this change: 351 tests passed locally on .NET 10.
MOD and in-game test DLL compilation against reference assemblies: passed, zero warnings/errors.
Compilation against locally cached actual game DLLs also passed with zero warnings/errors; no game binaries were added to git.
The new game cases are compiled, **not run** in an actual RimWorld session.

## Save compatibility and retry semantics

The JSON ledger has additive fields (`PaymentPending`, `NextAttemptTick`), defaulting to false/zero in old saves. Physical batches use a separate `outputBatches` Scribe node; it is empty in old saves. Item ownership is deep-scribed exactly once. After a handoff succeeds, the native map/pod owns the item; a failed handoff reattaches the same remaining item to the batch. Completed, empty batches are removed.

Economic effects are retained after repeated exceptions rather than discarding a physical remainder; after five exceptions subsequent attempts are separated by one in-game hour. The original exception is logged once per effect. Other effects, including return-pod creation, retain the existing abandonment policy. Failed return pods can be re-requested without extending the return deadline.

Before commitment, debt remains in the ledger and blocks new contracts. Partial reservation survives retries; a failed reservation never clears the debt. If the target map or creditor disappears, held silver is refunded at a remaining home map before reopening the debt (if it still exists). A new request gets a new payment ID.

## Manual acceptance still required

Run Dev actions → Rental Warehouse tests → Review boundary cases (#24-31) on a disposable development save. Also run the existing delivery and settlement cases.

1. Pause a rejected/partial output, save, exit, reload and unblock placement; total physical quantity must be conserved.
2. Repeat for staged silver, claimed silver in flight, and a registered unplaced delivery crate. Exactly one crate/payment must arrive.
3. Fail pod creation five times, restore placement, and request again. Deadline stays unchanged.
4. Leave an expired site with newly carried boxes at the check boundary; the taken boxes must still open after closure. The normal caravan's time on the same tile may avoid the original timing window.
5. Leave a damaged box on a site, revisit before closure, and verify identical HP; destroyed boxes must not return.
6. Read early-return quotes before/after claiming an advance and at maturity, then compare actual settlement. Quote creation must not alter the live ledger.
