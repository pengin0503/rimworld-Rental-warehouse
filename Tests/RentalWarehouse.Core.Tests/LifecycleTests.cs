using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class LifecycleTests
    {
        private static WorldSnapshot Snapshot(WorldState world) => new WorldSnapshot
        {
            MapIds = { MapId },
            FactionIds = { Depositor, Pursuer, "Faction_3" },
            HostileFactionIds = { Pursuer },
            DefNames = { "Steel", "Gold" },
        };

        private static (WorldState World, ContractState A, ContractState B) TwoActive()
        {
            var a = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var b = Terms(ContractKind.Normal, "K2", new[] { 1000m });
            var world = World(a, b);
            var ca = Activate(world, a);
            var cb = Activate(world, b);
            b.DepositorFactionId = "Faction_3";
            foreach (var r in world.Cargo.Values) r.Manifest.Add(new ManifestEntry { ThingDefName = "Steel", Count = 5 });
            return (world, ca, cb);
        }

        [Fact]
        public void DepositorGone_SystemCancelsWithoutPenalty_KeepsEarnedFees_OthersContinue()
        {
            var (world, a, b) = TwoActive();
            RunUntil(a, a.ActiveTick!.Value + 2 * D);
            RunUntil(b, b.ActiveTick!.Value + 2 * D);
            var snap = Snapshot(world);
            snap.FactionIds.Remove(Depositor);

            var d = LifecycleService.Reconcile(world, snap, a.ActiveTick!.Value + 2 * D);

            Assert.Equal(ContractPhase.SystemCancelled, a.Phase);
            Assert.Equal(20m, a.Settlement!.Credit);
            Assert.Equal(0m, a.Settlement.CompletionBonus);
            Assert.Equal(0m, a.Settlement.Compensation);
            Assert.Equal(0, a.Settlement.GoodwillDelta);
            Assert.Contains(d.Effects, e => e.Kind == EffectKind.RemoveCargo && e.EntityId == a.Terms.CargoIds[0]);
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
            Assert.Equal(ContractPhase.Active, b.Phase);
        }

        [Fact]
        public void Reconcile_Twice_DoesNotRepeat()
        {
            var (world, a, _) = TwoActive();
            var snap = Snapshot(world);
            snap.FactionIds.Remove(Depositor);

            LifecycleService.Reconcile(world, snap, 10);
            var again = LifecycleService.Reconcile(world, snap, 20);

            Assert.Empty(again.Effects);
        }

        [Fact]
        public void DepositorTurnedHostile_OffersThreeDayPenaltyFreeReturn()
        {
            var (world, a, _) = TwoActive();
            long t = a.ActiveTick!.Value + 3 * D;
            RunUntil(a, t);
            var snap = Snapshot(world);
            snap.HostileFactionIds.Add(Depositor);

            LifecycleService.Reconcile(world, snap, t);
            LifecycleService.Reconcile(world, snap, t + 250);

            Assert.Equal(ContractPhase.Returning, a.Phase);
            Assert.Equal(ReturnReason.DepositorHostile, a.ReturnReason);
            Assert.Equal(t + 3 * D, a.ReturnDeadlineTick);
            ReturnService.RequestPod(a, t + 1);
            ReturnService.Dispatch(a, a.Terms.CargoIds, t + 2);
            Assert.Equal(0m, a.Settlement!.CancellationFee);
            Assert.Equal(0, a.Settlement.GoodwillDelta);
            Assert.Equal(30m, a.Settlement.Credit);
        }

        [Fact]
        public void HostileDepositor_OffersAreWithdrawn()
        {
            var world = World(Terms(ContractKind.Normal, "K9", new[] { 800m }));
            var snap = Snapshot(world);
            snap.HostileFactionIds.Add(Depositor);

            LifecycleService.Reconcile(world, snap, 10);

            Assert.Empty(world.Offers);
            Assert.False(world.Cargo.ContainsKey("K9-C0"));
        }

        [Fact]
        public void PlayerAbandonedMap_SettlesRemainingCratesAsOrdinaryLoss()
        {
            var (world, a, b) = TwoActive();
            RunUntil(a, a.ActiveTick!.Value + D);
            var snap = Snapshot(world);
            snap.MapIds.Clear();
            snap.RemovedMapCauses[MapId] = MapRemovalCause.AbandonedByPlayer;

            LifecycleService.Reconcile(world, snap, a.ActiveTick!.Value + D);

            Assert.Equal(ContractPhase.Settled, a.Phase);
            Assert.Equal(LossCause.Lost, a.CargoStates[0].Loss);
            Assert.True(a.Settlement!.Compensation > 0m);
            Assert.True(a.Settlement.Debt > 0m || a.Settlement.Credit >= 0m);
            Assert.Equal(ContractPhase.Settled, b.Phase);
        }

        [Fact]
        public void PlayerAbandonedMap_BeforeDelivery_CancelsWithoutPenalty()
        {
            var t = Terms(ContractKind.Normal, "K1", new[] { 900m });
            var world = World(t);
            ContractEngine.Accept(t, Storage(4), world, 0);
            var snap = Snapshot(world);
            snap.MapIds.Clear();
            snap.RemovedMapCauses[MapId] = MapRemovalCause.AbandonedByPlayer;

            var d = LifecycleService.Reconcile(world, snap, 100);

            Assert.Equal(ContractPhase.Cancelled, world.Contract("K1")!.Phase);
            Assert.Equal("RW_Cancel_MapAbandoned", world.Contract("K1")!.CancelReason);
            Assert.Single(d.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_CancelledMapAbandoned");
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
        }

        [Fact]
        public void HostileDepositor_BeforeDelivery_CancelsWithReasonAndOneNotice()
        {
            var t = Terms(ContractKind.Normal, "K1", new[] { 900m });
            var world = World(t);
            ContractEngine.Accept(t, Storage(4), world, 0);
            var snap = Snapshot(world);
            snap.HostileFactionIds.Add(Depositor);

            var d = LifecycleService.Reconcile(world, snap, 100);
            var again = LifecycleService.Reconcile(world, snap, 200);

            var c = world.Contract("K1")!;
            Assert.Equal(ContractPhase.Cancelled, c.Phase);
            Assert.Equal("RW_Cancel_DepositorHostile", c.CancelReason);
            Assert.Single(d.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_CancelledDepositorHostile");
            Assert.Empty(again.Effects);
        }

        [Fact]
        public void DepositorGone_BeforeDelivery_SystemCancelsWithReasonAndOneNotice()
        {
            var t = Terms(ContractKind.Normal, "K1", new[] { 900m });
            var world = World(t);
            ContractEngine.Accept(t, Storage(4), world, 0);
            var snap = Snapshot(world);
            snap.FactionIds.Remove(Depositor);

            var d = LifecycleService.Reconcile(world, snap, 100);
            var again = LifecycleService.Reconcile(world, snap, 200);

            var c = world.Contract("K1")!;
            Assert.Equal(ContractPhase.SystemCancelled, c.Phase);
            Assert.Equal("RW_Cancel_DepositorGone", c.CancelReason);
            Assert.Single(d.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_DepositorGone");
            Assert.Empty(again.Effects);
        }

        [Fact]
        public void PlayerCancel_RecordsPlayerReason()
        {
            var t = Terms(ContractKind.Normal, "K1", new[] { 900m });
            var world = World(t);
            ContractEngine.Accept(t, Storage(4), world, 0);

            ContractEngine.Cancel(world.Contract("K1")!, 10);

            Assert.Equal(ContractEngine.CancelByPlayer, world.Contract("K1")!.CancelReason);
        }

        [Fact]
        public void MapVanishedForUnknownReason_SystemCancelsWithDiagnostic()
        {
            var (world, a, _) = TwoActive();
            var snap = Snapshot(world);
            snap.MapIds.Clear();
            snap.RemovedMapCauses[MapId] = MapRemovalCause.Unknown;

            var d = LifecycleService.Reconcile(world, snap, a.ActiveTick!.Value + 10);

            Assert.Equal(ContractPhase.SystemCancelled, a.Phase);
            Assert.Contains(d.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Diag_MapVanished");
        }

        [Fact]
        public void MapNotYetLoaded_IsNotTreatedAsRemoved()
        {
            var (world, a, _) = TwoActive();
            var snap = Snapshot(world);
            snap.MapIds.Clear();

            LifecycleService.Reconcile(world, snap, 100);

            Assert.Equal(ContractPhase.Active, a.Phase);
        }

        [Fact]
        public void MissingDef_CancelsOnlyThatContract()
        {
            var (world, a, b) = TwoActive();
            world.Cargo[a.Terms.CargoIds[0]].Manifest.Add(new ManifestEntry { ThingDefName = "RemovedModGun", Count = 1 });

            var d = LifecycleService.Reconcile(world, Snapshot(world), a.ActiveTick!.Value + 10);

            Assert.Equal(ContractPhase.SystemCancelled, a.Phase);
            Assert.Equal(ContractPhase.Active, b.Phase);
            Assert.Contains(d.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Diag_MissingDefs");
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.SpawnSilver || e.Kind == EffectKind.DeliverCargo);
        }

        [Fact]
        public void DuplicateContractId_QuarantinesTheDuplicate()
        {
            var (world, a, b) = TwoActive();
            var dup = a.Clone();
            world.Contracts.Add(dup);

            var d = LifecycleService.Reconcile(world, Snapshot(world), 10);

            Assert.Equal(2, world.Contracts.Count);
            Assert.Single(world.Quarantine);
            Assert.Same(dup, world.Quarantine[0]);
            Assert.Contains(d.Effects, e => e.Payload.TryGetValue("key", out var k) && k == "RW_Diag_DuplicateId");
        }

        [Fact]
        public void CreditorGone_ClosesDebtWithoutPenalty()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "Debt-K1", ContractId = "K1", FactionId = "Faction_77", Amount = 10m });

            var d = LifecycleService.Reconcile(world, Snapshot(world), 10);

            Assert.Empty(world.Debts);
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
            Assert.False(world.FactionBlockedUntil.ContainsKey("Faction_77"));
        }

        [Fact]
        public void History_IsPrunedButActiveWorkAndRecentHistoryStay()
        {
            var world = new WorldState();
            for (int i = 0; i < 40; i++)
            {
                var c = new ContractState { Terms = new ContractTerms { ContractId = "Old" + i }, Phase = ContractPhase.Settled, LastAccrualTick = i, Settlement = new SettlementResult() };
                world.Contracts.Add(c);
                world.Cargo["Old" + i + "-0"] = new CargoRecord { CargoId = "Old" + i + "-0", ContractId = "Old" + i, Custody = CargoCustody.Returned };
                world.Effects.TryBegin("Old" + i + ":settle:goodwill", i);
                world.Effects.Complete("Old" + i + ":settle:goodwill", i);
            }
            world.Contracts.Add(new ContractState
            {
                Terms = new ContractTerms { ContractId = "Live", DepositorFactionId = Depositor },
                Phase = ContractPhase.Active,
                MapId = MapId,
                ActiveTick = 0,
                EndTick = 200 * D,
            });
            world.Effects.TryBegin("Live:deliver:x", 0);
            world.Effects.Complete("Live:deliver:x", 0);

            LifecycleService.Reconcile(world, Snapshot(world), 100 * D);

            Assert.Equal(LifecycleService.SettledHistoryKept + 1, world.Contracts.Count);
            Assert.Contains(world.Contracts, c => c.Terms.ContractId == "Live");
            Assert.Contains(world.Contracts, c => c.Terms.ContractId == "Old39");
            Assert.DoesNotContain(world.Contracts, c => c.Terms.ContractId == "Old0");
            Assert.False(world.Cargo.ContainsKey("Old0-0"));
            Assert.True(world.Effects.IsCompleted("Live:deliver:x"));
            Assert.False(world.Effects.Records.ContainsKey("Old0:settle:goodwill"));
        }

        [Fact]
        public void History_PruneKeepsUnclaimedBalances_UntilClaimed()
        {
            var world = new WorldState();
            for (int i = 0; i < 21; i++)
            {
                world.Contracts.Add(new ContractState
                {
                    Terms = new ContractTerms { ContractId = "Old" + i, DepositorFactionId = Depositor },
                    Phase = ContractPhase.Settled,
                    MapId = MapId,
                    LastAccrualTick = i,
                    Receivable = 72m,
                    Settlement = new SettlementResult { Credit = 72m },
                });
            }

            LifecycleService.Reconcile(world, Snapshot(world), 100 * D);

            Assert.Equal(21, world.Contracts.Count);
            Assert.Equal(1512m, world.ReceivableBalance());
            var claim = PaymentService.Claim(world, MapId, "Cell_1_1", 100 * D);
            Assert.Equal(1512m, claim.Effects.Single().Amount);
            Assert.False(PaymentService.Claim(world, MapId, "Cell_1_1", 100 * D).Success);

            LifecycleService.Reconcile(world, Snapshot(world), 100 * D);

            Assert.Equal(LifecycleService.SettledHistoryKept, world.Contracts.Count);
        }

        [Fact]
        public void ClosedSites_ArePrunedButStolenCratesStay()
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", 3, SiteTier.Small, "Faction_2", 1, SiteTests.Ctx(0))!;
            world.Sites.Add(site);
            CargoService.TakeFromSite(world.Cargo[site.CargoIds[0]]);
            SiteService.Close(world, site, D);

            LifecycleService.Reconcile(world, Snapshot(world), D + LifecycleService.ClosedSiteRetention + 1);

            Assert.Empty(world.Sites);
            Assert.True(world.Cargo.ContainsKey(site.CargoIds[0]));
        }

        [Fact]
        public void MidGameInstall_InitializesOnce()
        {
            var world = new WorldState();

            Assert.True(LifecycleService.Initialize(world, worldSeed: 123, now: 5000));
            Assert.False(LifecycleService.Initialize(world, worldSeed: 999, now: 9000));

            Assert.Equal(123, world.WorldSeed);
            Assert.Equal(5000, world.InstalledTick);
            Assert.False(world.BusinessOpen);
            Assert.Null(world.NextOfferTick);
            Assert.Null(world.NextSiteTick);
        }

        [Fact]
        public void MidGameInstall_SurvivesSaveLoadWithoutReinitializing()
        {
            var world = new WorldState();
            LifecycleService.Initialize(world, 123, 5000);

            var loaded = StateCodec.Import(Migration.Upgrade(StateCodec.Export(world)));

            Assert.False(LifecycleService.Initialize(loaded, 777, 9999));
            Assert.Equal(123, loaded.WorldSeed);
        }

        [Fact]
        public void Migration_IsIdempotentAndKeepsCurrentSchema()
        {
            var world = World(Terms(ContractKind.Normal, "K1", new[] { 700m }));
            var saved = StateCodec.Export(world);

            var once = Migration.Upgrade(saved);
            var twice = Migration.Upgrade(once);

            Assert.Equal(WorldState.CurrentSchemaVersion, once.SchemaVersion);
            Assert.Equal(once.Payload, twice.Payload);
            Assert.Equal(saved.Payload, once.Payload);
        }

        [Fact]
        public void Migration_V1Schedule_ContinuesBatchAndSiteNumbering()
        {
            var world = new WorldState { WorldSeed = 5, BusinessOpen = true, BusinessStartTick = 1000 };
            // Version 1: offers at start+1d every 2 days, intel at start+2d every 5 days; 3 batches and 2 reports done.
            world.NextOfferTick = 1000 + D + 3 * 2 * D;
            world.NextSiteTick = 1000 + 2 * D + 2 * 5 * D;
            world.SchemaVersion = 1;
            var v1 = StateCodec.Export(world);

            var upgraded = Migration.Upgrade(v1);
            var loaded = StateCodec.Import(upgraded);

            Assert.Equal(2, upgraded.SchemaVersion);
            Assert.Equal(2, loaded.SchemaVersion);
            Assert.Equal(3, loaded.NextOfferBatch);
            Assert.Equal(2, loaded.NextSiteIndex);
            Assert.Equal(world.NextOfferTick, loaded.NextOfferTick);
            Assert.Equal(world.NextSiteTick, loaded.NextSiteTick);
            Assert.Equal(upgraded.Payload, Migration.Upgrade(upgraded).Payload);
        }

        [Fact]
        public void Migration_V1NeverOpened_StartsNumberingAtZero()
        {
            var world = new WorldState { WorldSeed = 5, SchemaVersion = 1 };

            var loaded = StateCodec.Import(Migration.Upgrade(StateCodec.Export(world)));

            Assert.Equal(0, loaded.NextOfferBatch);
            Assert.Equal(0, loaded.NextSiteIndex);
            Assert.Equal(WorldState.CurrentSchemaVersion, loaded.SchemaVersion);
        }

        [Fact]
        public void Migration_EmptyLedgerFromOldSave_BecomesFreshCurrent()
        {
            var upgraded = Migration.Upgrade(new SavedState { SchemaVersion = 0, Payload = "" });

            Assert.Equal(WorldState.CurrentSchemaVersion, upgraded.SchemaVersion);
            Assert.Empty(StateCodec.Import(upgraded).Contracts);
        }
    }
}
