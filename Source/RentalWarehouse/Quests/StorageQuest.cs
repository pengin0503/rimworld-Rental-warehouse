using System.Collections.Generic;
using RentalWarehouse.Core;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Drives accepted contracts through delivery, storage and the active term every 250 ticks.</summary>
    public static class StorageQuest
    {
        public static void Tick(WarehouseWorldComponent world, long now)
        {
            var snapshots = new Dictionary<string, StorageSnapshot>();
            foreach (var contract in world.State.Contracts.ToArray())
            {
                if (contract.IsTerminal) continue;
                ReturnService.RecoverMissingPod(world.State, contract,
                    ReturnAdapter.FindPod(Ids.Map(contract.MapId), contract.ActiveReturnPodId) != null);
                if (Ids.Map(contract.MapId) == null) continue; // handled by the lifecycle reconciler
                if (!snapshots.TryGetValue(contract.MapId, out var snapshot))
                {
                    snapshot = StorageAdapter.Capture(contract.MapId);
                    snapshots[contract.MapId] = snapshot;
                }
                world.Enqueue(ContractEngine.Advance(contract, snapshot, now).Effects, applyNow: false);
                if (contract.Phase == ContractPhase.Active && contract.BaselineThreatPoints <= 0m)
                {
                    // Spec 6: dedicated raid size is fixed from the ordinary threat points at activation.
                    contract.BaselineThreatPoints = (decimal)RimWorld.StorytellerUtility.DefaultThreatPointsNow(Ids.Map(contract.MapId));
                }
                if (contract.IsTerminal) RemoveLeftoverPod(contract);
            }
            PaymentService.SyncDebts(world.State, now);
        }

        /// <summary>A pod still standing when its contract settles is taken away; crates inside were already settled.</summary>
        public static void RemoveLeftoverPod(ContractState contract)
        {
            var pod = ReturnAdapter.FindPod(Ids.Map(contract.MapId), contract.ActiveReturnPodId);
            if (pod != null && !pod.Destroyed) pod.Destroy(DestroyMode.Vanish);
        }

        /// <summary>Accepts an offer for storage on <paramref name="map"/>. A reception desk on that map is required (spec 3).</summary>
        public static Decision<WorldState> Accept(WarehouseWorldComponent world, ContractTerms terms, Map map)
        {
            if (map == null || !map.IsPlayerHome) return Decision<WorldState>.Fail("RW_Reject_NotHomeMap", world.State);
            if (!Building_Reception.AnyOn(map)) return Decision<WorldState>.Fail("RW_Reject_NoReception", world.State);
            var decision = ContractEngine.Accept(terms, StorageAdapter.Capture(Ids.Of(map)), world.State, Ids.Now);
            if (decision.Success) world.Enqueue(decision.Effects);
            return decision;
        }
    }
}
