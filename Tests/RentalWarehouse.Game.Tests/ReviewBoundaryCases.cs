using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>Run in a development save; quantity conservation and site writeback against real game Things.</summary>
    public static class ReviewBoundaryCases
    {
        [DebugAction("Rental Warehouse tests", "Review boundary cases (#24-31)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("ReviewBoundary", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("RejectedOutputKeepsTheSamePhysicalStack", RejectedOutput);
            yield return ("ExceptionAfterPlacementDoesNotDuplicateOutput", ExceptionAfterPlacement);
            yield return ("PartiallyMergedStackRetainsOnlyTheRemainder", PartialRemainder);
            yield return ("UnplacedRegisteredCrateMustActuallyBeDelivered", UnplacedDelivery);
            yield return ("SiteWritebackPreservesDamagedCrateHitPoints", DamagedWriteback);
            yield return ("SiteClosureSynchronizesPlayerHeldCratesFirst", OwnershipBeforeClosure);
        }

        private static OutputBatch SilverBatch(int amount)
        {
            var batch = new OutputBatch();
            batch.Prepare(() =>
            {
                var t = ThingMaker.MakeThing(ThingDefOf.Silver);
                t.stackCount = amount;
                return new List<Thing> { t };
            });
            return batch;
        }

        private static void RejectedOutput(GameTestScope s)
        {
            var batch = SilverBatch(20);
            var item = batch.Contents[0];
            Check(!batch.Transfer(_ => { }), "failed placement remains incomplete");
            Check(ReferenceEquals(item, batch.Contents[0]), "original stack retained");
            batch.Prepare(() => throw new GameTestFailure("must not recreate the contents"));
            string xml = Scribe.saver.DebugOutputFor(batch);
            Check(xml.Contains("contents") && xml.Contains("Silver"), "pending physical contents are saved");
            foreach (var t in batch.Contents.ToList()) t.Destroy();
        }

        private static void ExceptionAfterPlacement(GameTestScope s)
        {
            var batch = SilverBatch(20);
            var item = batch.Contents[0];
            var cell = s.FindClearRect(2).Min;
            try
            {
                batch.Transfer(t => { s.Spawn(t, cell); throw new InvalidOperationException("after placement"); });
                throw new GameTestFailure("injected exception was not raised");
            }
            catch (InvalidOperationException) { }
            Check(item.Spawned, "first handoff really placed the stack");
            Equal(0, batch.Contents.Count, "placed item is not retained");
            Check(batch.Transfer(_ => throw new GameTestFailure("must not transfer twice")), "retry completes without output");
            Equal(20, item.stackCount, "exactly the original quantity exists");
        }

        private static void PartialRemainder(GameTestScope s)
        {
            var batch = SilverBatch(20);
            var cell = s.FindClearRect(2).Min;
            try
            {
                batch.Transfer(t => { s.Spawn(t.SplitOff(7), cell); throw new InvalidOperationException("partial"); });
            }
            catch (InvalidOperationException) { }
            Equal(13, batch.Contents.Sum(t => t.stackCount), "only the remainder is retained");
            Check(batch.Transfer(t => s.Spawn(t, cell + IntVec3.North)), "remainder can be placed");
        }

        private static void UnplacedDelivery(GameTestScope s)
        {
            var terms = DeliveryCases.Offer(s, ContractKind.Normal, 500m);
            s.TrackContract(terms.ContractId);
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            var contract = new ContractState { Terms = terms, MapId = Ids.Of(s.Map), Phase = ContractPhase.AwaitingDelivery };
            var id = terms.CargoIds[0];
            contract.CargoStates.Add(new ContractCargoState { CargoId = id, DeclaredValue = 500 });
            s.World.State.Contracts.Add(contract);
            var crate = CargoAdapter.MakeCrate(s.World.State.Cargo[id]);
            s.World.RegisterCrate(id, crate);
            var effect = new EffectRequest { EffectId = terms.ContractId + ":deliver:test", Kind = EffectKind.DeliverCargo, EntityId = id, Payload = { ["contract"] = terms.ContractId } };
            Check(DeliveryAdapter.Deliver(s.World, effect), "delivery completed");
            Check(crate.MapHeld == s.Map, "unplaced registration alone was not accepted as delivery");
            Check(ReferenceEquals(crate, s.World.FindCrate(id)), "same crate reused");
            Equal(true, contract.CargoStates[0].Delivered, "ledger delivered only after handoff");
        }

        private static void DamagedWriteback(GameTestScope s)
        {
            var crate = s.SpawnCrate(s.FindClearRect(2).Min, "Faction_GT", CargoCustody.Depositor, 500, StorageKind.Vault);
            var record = CargoAdapter.Comp(crate).Record;
            var site = new SiteRecord { SiteId = "GT-HP-" + record.CargoId, FactionId = "Faction_GT" };
            record.SiteId = site.SiteId;
            site.CargoIds.Add(record.CargoId);
            s.World.State.Sites.Add(site);
            try
            {
                crate.HitPoints = 20;
                var parent = new VaultSite { siteId = site.SiteId };
                VaultMapGenerator.WriteBack(parent, s.Map);
                Equal(20, record.HitPoints, "damaged HP written before disposal");
                var rebuilt = CargoAdapter.MakeCrate(record);
                Equal(20, rebuilt.HitPoints, "recreated HP");
                CargoAdapter.RemoveCrate(rebuilt);
            }
            finally { s.World.State.Sites.Remove(site); }
        }

        private static void OwnershipBeforeClosure(GameTestScope s)
        {
            var crate = s.SpawnCrate(s.FindClearRect(2).Min, "Faction_GT", CargoCustody.Depositor, 500, StorageKind.Vault);
            var record = CargoAdapter.Comp(crate).Record;
            var site = new SiteRecord { SiteId = "GT-Ownership-" + record.CargoId, FactionId = "Faction_GT", Visited = true, FirstEntryTick = 0 };
            record.SiteId = site.SiteId;
            site.CargoIds.Add(record.CargoId);
            s.World.State.Sites.Add(site);
            try
            {
                Check(VaultDiscovery.CloseIfDue(s.World, site, new SiteContext { NowTick = 100 * CoreTime.TicksPerDay }), "site closed");
                Check(s.World.State.Cargo.ContainsKey(record.CargoId), "physical player-held box kept");
                Equal(CargoCustody.Player, record.Custody, "ownership synchronized before pruning");
            }
            finally { s.World.State.Sites.Remove(site); }
        }
    }
}
