using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// World-wide ledger holder (spec 9). Owns the Core <see cref="WorldState"/>; all business state lives there, not in
    /// game references. Also keeps a runtime index from cargo ID to crate Thing so nothing scans all Things.
    /// </summary>
    public class WarehouseWorldComponent : WorldComponent
    {
        public WorldState State = new WorldState();

        private readonly Dictionary<string, Thing> crates = new Dictionary<string, Thing>();

        public WarehouseWorldComponent(World world) : base(world)
        {
        }

        public static WarehouseWorldComponent Instance => Find.World?.GetComponent<WarehouseWorldComponent>();

        public void RegisterCrate(string cargoId, Thing crate)
        {
            if (cargoId == null || crate == null) return;
            crates[cargoId] = crate;
        }

        public void UnregisterCrate(string cargoId, Thing crate)
        {
            if (cargoId != null && crates.TryGetValue(cargoId, out var current) && current == crate) crates.Remove(cargoId);
        }

        public Thing FindCrate(string cargoId)
        {
            if (cargoId == null || !crates.TryGetValue(cargoId, out var crate)) return null;
            return crate.Destroyed ? null : crate;
        }

        public IEnumerable<KeyValuePair<string, Thing>> AllCrates => crates;

        public void SetBusinessOpen(bool open)
        {
            State.BusinessOpen = open;
            if (open && State.BusinessStartTick == null) State.BusinessStartTick = Ids.Now;
        }

        /// <summary>A crate was destroyed by something other than the warehouse itself.</summary>
        public void Notify_CrateDestroyed(string cargoId)
        {
            if (cargoId == null || !State.Cargo.TryGetValue(cargoId, out var record)) return;
            CargoService.ApplyHitPoints(record, 0);
            var contract = record.ContractId == null ? null : State.Contract(record.ContractId);
            if (contract != null) ContractEngine.MarkLost(contract, cargoId, LossCause.Lost, Ids.Now);
        }

        /// <summary>Deliberate opening of a crate held for another faction: spec 3 "横領".</summary>
        public void Embezzle(string cargoId)
        {
            if (!State.Cargo.TryGetValue(cargoId, out var record) || record.ContractId == null) return;
            var contract = State.Contract(record.ContractId);
            if (contract == null) return;
            var crate = FindCrate(cargoId);
            if (crate == null || !crate.Spawned) return;
            if (!ContractEngine.MarkLost(contract, cargoId, LossCause.Embezzled, Ids.Now).Success) return;
            if (CargoService.Open(record, "open:" + cargoId).Success) OpenCrate(crate, record);
        }

        /// <summary>Opening a crate stolen from an NPC vault: the contents are the reward.</summary>
        public void OpenStolen(string cargoId)
        {
            if (!State.Cargo.TryGetValue(cargoId, out var record) || record.Custody != CargoCustody.Player) return;
            var crate = FindCrate(cargoId);
            if (crate == null || !crate.Spawned) return;
            if (CargoService.Open(record, "open:" + cargoId).Success) OpenCrate(crate, record);
        }

        private static void OpenCrate(Thing crate, CargoRecord record)
        {
            var map = crate.Map;
            var pos = crate.Position;
            var contents = CargoAdapter.MakeContents(record, out var missing);
            CargoAdapter.RemoveCrate(crate);
            foreach (var thing in contents) GenPlace.TryPlaceThing(thing, pos, map, ThingPlaceMode.Near);
            if (missing.Count > 0) Log.Warning("[RentalWarehouse] Crate " + record.CargoId + " had unknown defs: " + string.Join(", ", missing));
        }
    }
}
