using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Delivers contract crates by drop pod (Core game API, no DLC; see docs/technical/1.7-api-audit.md).</summary>
    public static class DeliveryAdapter
    {
        /// <summary>
        /// One crate per effect. Reconciles first: a crate with this cargo ID that already exists (in flight or on the
        /// map) counts as delivered, so a retry after an exception never drops a second copy.
        /// </summary>
        public static bool Deliver(WarehouseWorldComponent world, EffectRequest effect)
        {
            string cargoId = effect.EntityId;
            if (!world.State.Cargo.TryGetValue(cargoId, out var record)) return true;
            effect.Payload.TryGetValue("contract", out var contractId);
            var contract = contractId == null ? null : world.State.Contract(contractId);
            if (contract == null || contract.IsTerminal) return true;
            var existing = contract.Cargo(cargoId);
            if (existing == null || existing.Delivered || existing.Loss != LossCause.None) return true;

            long now = Ids.Now;
            if (world.FindCrate(cargoId) != null)
            {
                ContractEngine.MarkDelivered(contract, cargoId, now);
                return true;
            }
            var map = Ids.Map(contract.MapId);
            if (map == null) return false;

            var spot = DeliverySpot(map);
            var crate = CargoAdapter.MakeCrate(record);
            world.RegisterCrate(cargoId, crate);
            DropPodUtility.DropThingsNear(spot, map, new List<Thing> { crate }, openDelay: 110, canInstaDropDuringInit: false, leaveSlag: false, canRoofPunch: false, forbid: false, allowFogged: false);
            bool first = !contract.CargoStates.Exists(c => c.Delivered);
            ContractEngine.MarkDelivered(contract, cargoId, now);
            if (first)
            {
                Find.LetterStack.ReceiveLetter("RW_Letter_DeliveryTitle".Translate(), "RW_Letter_Delivery".Translate(contract.Terms.ContractId, contract.Terms.CargoIds.Count),
                    LetterDefOf.PositiveEvent, new TargetInfo(spot, map));
            }
            return true;
        }

        /// <summary>Near the reception desk if possible, otherwise the map's trade drop spot (spec 4 "代替地点").</summary>
        public static IntVec3 DeliverySpot(Map map)
        {
            foreach (var b in map.listerBuildings.AllBuildingsColonistOfDef(RWDefOf.RW_Reception))
            {
                if (DropCellFinder.TryFindDropSpotNear(b.Position, map, out var near, allowFogged: false, canRoofPunch: false, allowIndoors: false))
                {
                    return near;
                }
            }
            return DropCellFinder.TradeDropSpot(map);
        }
    }
}
