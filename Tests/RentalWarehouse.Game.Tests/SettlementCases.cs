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
    /// <summary>A02/A03/A06/A10/A11 accounting parts that need the game: silver in and out, return pod, wealth.</summary>
    public static class SettlementCases
    {
        [DebugAction("Rental Warehouse tests", "Settlement cases (A02/A03/A06/A10)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Settlement", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("ReceivableCountsAsWealthOnce", ReceivableCountsAsWealth);
            yield return ("ClaimDropsSilverOnce", ClaimDropsSilverOnce);
            yield return ("DebtPaymentConsumesExactAmount", DebtPaymentConsumesExactAmount);
            yield return ("ReturnPodIsCreatedOnceAndSendReturnsLoadedCrates", ReturnPodFlow);
            yield return ("ReturnPodsAcceptOnlyTheirOwnContract", ReturnPodsAcceptOnlyTheirOwnContract);
        }

        private static ContractState ReturningContract(GameTestScope s, decimal value)
        {
            var terms = DeliveryCases.Offer(s, ContractKind.Normal, value);
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            var c = new ContractState
            {
                Terms = terms,
                Phase = ContractPhase.Returning,
                MapId = Ids.Of(s.Map),
                ReturnReason = ReturnReason.Matured,
                ReturnDeadlineTick = Ids.Now + Limits.ReturnGrace,
                LastAccrualTick = Ids.Now,
            };
            foreach (var id in terms.CargoIds) c.CargoStates.Add(new ContractCargoState { CargoId = id, DeclaredValue = value, Delivered = true });
            s.World.State.Contracts.Add(c);
            return c;
        }

        private static void ReceivableCountsAsWealth(GameTestScope s)
        {
            Check(HarmonyGate.Active, "Harmony patches are active");
            var c = ReturningContract(s, 500m);
            s.Map.wealthWatcher.ForceRecount();
            float before = s.Map.wealthWatcher.WealthTotal;
            c.Receivable = 300m;
            s.Map.wealthWatcher.ForceRecount();
            float after = s.Map.wealthWatcher.WealthTotal;
            c.Receivable = 0m;
            Check(Math.Abs(after - before - 300f) < 1f, "wealth rose by " + (after - before) + ", expected 300");
        }

        private static void ClaimDropsSilverOnce(GameTestScope s)
        {
            var c = ReturningContract(s, 500m);
            c.Receivable = 77m;
            var d = PaymentService.Claim(s.World.State, Ids.Of(s.Map), PaymentAdapter.DefaultDestination(s.Map), Ids.Now);
            Check(d.Success, "claim: " + d.ReasonKey);
            int pods = s.Map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).Count;
            s.World.Enqueue(d.Effects);
            s.World.Enqueue(d.Effects);
            int after = s.Map.listerThings.ThingsInGroup(ThingRequestGroup.ActiveTransporter).Count;
            Check(after - pods <= 1, "at most one pod per claim (" + (after - pods) + ")");
            Equal(0m, c.Receivable, "receivable after claim");
        }

        private static void DebtPaymentConsumesExactAmount(GameTestScope s)
        {
            var area = s.FindClearRect(2);
            s.Map.areaManager.Home[area.Min] = true;
            var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = 100;
            s.Spawn(silver, area.Min);
            int before = PaymentAdapter.SpendableSilverCount(s.Map);
            s.World.State.Debts.Add(new Debt { DebtId = "GT-Debt", ContractId = "GT", FactionId = "Faction_GT", Amount = 34m });

            var d = PaymentService.ResolveDebt(s.World.State, "GT-Debt", refuse: false, Ids.Now, Ids.Of(s.Map));
            s.World.Enqueue(d.Effects);
            s.World.Enqueue(d.Effects);

            Equal(before - 34, PaymentAdapter.SpendableSilverCount(s.Map), "silver after payment");
        }

        private static void ReturnPodsAcceptOnlyTheirOwnContract(GameTestScope s)
        {
            Check(HarmonyGate.Active, "Harmony patches are active");
            var area = s.FindClearRect(3);
            var pods = new List<Building_ReturnPod>();
            var crates = new List<Thing>();
            for (int i = 0; i < 2; i++)
            {
                var c = ReturningContract(s, 500m);
                s.TrackContract(c.Terms.ContractId);
                var crate = s.SpawnCrate(area.Cells.ElementAt(i), c.Terms.DepositorFactionId, CargoCustody.Depositor, 500m, StorageKind.Rack, c.Terms.ContractId);
                c.CargoStates.Clear();
                c.CargoStates.Add(new ContractCargoState { CargoId = CargoAdapter.Comp(crate).cargoId, DeclaredValue = 500m, Delivered = true });
                s.World.Enqueue(ReturnService.RequestPod(c, Ids.Now).Effects);
                pods.Add(s.Map.listerThings.ThingsOfDef(ReturnAdapter.PodDef).OfType<Building_ReturnPod>().Single(p => p.contractId == c.Terms.ContractId));
                crates.Add(crate);
            }

            Check(pods[0].Accepts(crates[0]) && pods[1].Accepts(crates[1]), "each pod accepts its own crate");
            Check(!pods[0].Accepts(crates[1]) && !pods[1].Accepts(crates[0]), "a pod refuses another contract's crate");
            Check(StoreUtility.TryFindBestBetterStoreCellFor(crates[1], null, s.Map, StoragePriority.Unstored, Faction.OfPlayer, out var cell), "a store cell exists");
            Check(pods[1].AllSlotCellsList().Contains(cell), "crate goes to its own contract's pod (got " + cell + ")");
            foreach (var pod in pods) pod.Destroy(DestroyMode.Vanish);
        }

        private static void ReturnPodFlow(GameTestScope s)
        {
            var c = ReturningContract(s, 500m);
            var area = s.FindClearRect(3);
            var crate = s.SpawnCrate(area.CenterCell, c.Terms.DepositorFactionId, CargoCustody.Depositor, 500m, StorageKind.Rack, c.Terms.ContractId);
            var id = CargoAdapter.Comp(crate).cargoId;
            c.CargoStates.Clear();
            c.CargoStates.Add(new ContractCargoState { CargoId = id, DeclaredValue = 500m, Delivered = true });

            var req = ReturnService.RequestPod(c, Ids.Now);
            s.World.Enqueue(req.Effects);
            s.World.Enqueue(req.Effects);
            var pods = s.Map.listerThings.ThingsOfDef(ReturnAdapter.PodDef).OfType<Building_ReturnPod>().Where(p => p.contractId == c.Terms.ContractId).ToList();
            Equal(1, pods.Count, "pods created");
            var pod = pods[0];
            crate.DeSpawn();
            GenSpawn.Spawn(crate, pod.Position, s.Map);

            ReturnAdapter.Send(pod);

            Check(pod.Destroyed, "pod leaves after sending");
            Check(crate.Destroyed, "crate leaves with the pod");
            Equal(ContractPhase.Settled, c.Phase, "contract settles when the last crate is returned");
        }
    }
}
