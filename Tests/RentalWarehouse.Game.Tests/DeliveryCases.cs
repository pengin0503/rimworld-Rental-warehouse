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
    /// <summary>A01/A14 game parts: reception requirement, delivery by pod, fallback drop spot, storage start.</summary>
    public static class DeliveryCases
    {
        [DebugAction("Rental Warehouse tests", "Delivery cases (A01)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Delivery", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("AcceptWithoutReceptionIsRefused", AcceptWithoutReception);
            yield return ("DeliverySpotFallsBackToTradeDropSpot", DeliverySpotFallback);
            yield return ("AcceptedContractDeliversEachCrateOnce", DeliversOnce);
        }

        internal static ContractTerms Offer(GameTestScope s, ContractKind kind, params decimal[] values)
        {
            var depositor = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && !f.Hidden && f.def.humanlikeFaction && !f.HostileTo(Faction.OfPlayer));
            string id = "GT-K-" + Rand.Int.ToString("X6");
            var cargo = new List<(string, decimal)>();
            for (int i = 0; i < values.Length; i++)
            {
                string cid = id + "-" + i;
                s.World.State.Cargo[cid] = new CargoRecord
                {
                    CargoId = cid,
                    ContractId = id,
                    OwnerFactionId = Ids.Of(depositor),
                    Custody = CargoCustody.Depositor,
                    DeclaredValue = values[i],
                    MassKg = 12m,
                    RequiredStorage = ContractParameters.For(kind).RequiredStorage,
                    Manifest = { new ManifestEntry { ThingDefName = "Steel", Count = 20 } },
                };
                cargo.Add((cid, values[i]));
            }
            var terms = ContractTermsFactory.Create(ContractParameters.For(kind), id, Ids.Of(depositor), null, cargo, 0, new WarehouseSettings(), 0, 1, Ids.Now);
            s.World.State.Offers.Add(new ContractState { Terms = terms, Phase = ContractPhase.Offered });
            s.TrackContract(id);
            return terms;
        }

        private static void AcceptWithoutReception(GameTestScope s)
        {
            if (Building_Reception.AnyOn(s.Map)) throw new GameTestFailure("run on a map without a reception desk");
            var terms = Offer(s, ContractKind.Normal, 500m);
            var d = StorageQuest.Accept(s.World, terms, s.Map);
            Equal("RW_Reject_NoReception", d.ReasonKey, "reason");
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
        }

        private static void DeliverySpotFallback(GameTestScope s)
        {
            var spot = DeliveryAdapter.DeliverySpot(s.Map);
            Check(spot.IsValid && spot.InBounds(s.Map), "a valid drop spot is always found");
        }

        private static void DeliversOnce(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            s.SpawnBuilding(RWDefOf.RW_Reception, room.Max);
            s.SpawnBuilding(RWDefOf.RW_StorageRack, room.Min);
            var terms = Offer(s, ContractKind.Normal, 400m, 400m);
            var accepted = StorageQuest.Accept(s.World, terms, s.Map);
            Check(accepted.Success, "accept: " + accepted.ReasonKey);
            var contract = s.World.State.Contract(terms.ContractId);
            contract.DeliveryTick = Ids.Now;

            var effects = ContractEngine.Advance(contract, StorageAdapter.Capture(Ids.Of(s.Map)), Ids.Now).Effects;
            s.World.Enqueue(effects);
            s.World.Enqueue(effects);

            Equal(ContractPhase.AwaitingStorage, contract.Phase, "phase after delivery");
            foreach (var id in terms.CargoIds)
            {
                Check(s.World.FindCrate(id) != null, "crate " + id + " exists (in flight or spawned)");
            }
            Equal(terms.CargoIds.Count, s.World.AllCrates.Count(kv => terms.CargoIds.Contains(kv.Key)), "one crate per cargo id");
            foreach (var id in terms.CargoIds)
            {
                var crate = s.World.FindCrate(id);
                if (crate != null) CargoAdapter.RemoveCrate(crate);
            }
        }
    }
}
