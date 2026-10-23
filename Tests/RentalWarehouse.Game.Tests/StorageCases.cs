using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>A01/A10 facility parts: capacity, valid storage, wrong facility, deconstruction, wealth, ejection, export.</summary>
    public static class StorageCases
    {
        private const string Owner = "Faction_GameTest";

        [DebugAction("Rental Warehouse tests", "Storage cases (A01/A10)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Storage", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("RackHoldsFourCratesWhenRoofedIndoors", RackHoldsFourCratesWhenRoofedIndoors);
            yield return ("OutdoorRackIsNotValidStorage", OutdoorRackIsNotValidStorage);
            yield return ("VaultCargoOnRackIsNotValid", VaultCargoOnRackIsNotValid);
            yield return ("FacilityWithCratesCannotBeDeconstructed", FacilityWithCratesCannotBeDeconstructed);
            yield return ("EntrustedCrateHasNoMarketValue_StolenCrateHasDeclaredValue", WealthValues);
            yield return ("DestroyedRackLeavesCratesOnTheGround", DestroyedRackLeavesCrates);
            yield return ("ContractCratesAreNotCaravanCandidates", ContractCratesAreNotCaravanCandidates);
        }

        private static Building_WarehouseStorage Rack(GameTestScope s, IntVec3 cell) =>
            (Building_WarehouseStorage)s.SpawnBuilding(RWDefOf.RW_StorageRack, cell);

        private static void RackHoldsFourCratesWhenRoofedIndoors(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var rack = Rack(s, room.Min);
            var cells = rack.AllSlotCellsList();
            var ids = new List<string>();
            foreach (var cell in cells)
            {
                for (int i = 0; i < 2; i++) ids.Add(CargoAdapter.Comp(s.SpawnCrate(cell, Owner, CargoCustody.Depositor, 500m, StorageKind.Rack, "GT-K"))!.cargoId);
            }

            var snapshot = StorageAdapter.Capture(Ids.Of(s.Map));
            var f = snapshot.Facilities.Single(x => x.FacilityId == rack.FacilityId);
            Check(f.IsValid, "roofed indoor rack should be valid: " + rack.FacilityProblem());
            Equal(0, f.FreeSlots, "free slots");
            Equal(4, ids.Count(snapshot.ValidStoredCargoIds.Contains), "valid stored crates");
        }

        private static void OutdoorRackIsNotValidStorage(GameTestScope s)
        {
            var area = s.FindClearRect(2);
            foreach (var c in area) s.Map.roofGrid.SetRoof(c, null);
            var rack = Rack(s, area.Min);
            var crate = s.SpawnCrate(rack.Position, Owner, CargoCustody.Depositor, 500m, StorageKind.Rack, "GT-K");

            var snapshot = StorageAdapter.Capture(Ids.Of(s.Map));
            Check(!snapshot.ValidStoredCargoIds.Contains(CargoAdapter.Comp(crate).cargoId), "outdoor crate must not earn fees");
            Check(StorageAdapter.FeeStopReason(crate, CargoAdapter.Comp(crate).Record) != null, "a fee stop reason is shown");
        }

        private static void VaultCargoOnRackIsNotValid(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var rack = Rack(s, room.Min);
            var crate = s.SpawnCrate(rack.Position, Owner, CargoCustody.Depositor, 3000m, StorageKind.Vault, "GT-K");

            Equal("RW_FeeStop_WrongFacility", StorageAdapter.FeeStopReason(crate, CargoAdapter.Comp(crate).Record), "fee stop reason");
        }

        private static void FacilityWithCratesCannotBeDeconstructed(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var rack = Rack(s, room.Min);
            Check(rack.DeconstructibleBy(Faction.OfPlayer).Accepted, "empty rack is deconstructible");
            s.SpawnCrate(rack.Position, Owner, CargoCustody.Depositor, 500m, StorageKind.Rack, "GT-K");
            Check(!rack.DeconstructibleBy(Faction.OfPlayer).Accepted, "rack holding a crate must refuse deconstruction");
        }

        private static void WealthValues(GameTestScope s)
        {
            var area = s.FindClearRect(2);
            var entrusted = s.SpawnCrate(area.Min, Owner, CargoCustody.Depositor, 1500m, StorageKind.Rack, "GT-K");
            var stolen = s.SpawnCrate(area.Max, CargoService.PlayerFactionId, CargoCustody.Player, 1500m, StorageKind.Vault);
            CargoAdapter.Comp(stolen).Record.Stolen = true;

            Equal(0f, entrusted.MarketValue, "entrusted crate market value");
            Check(Math.Abs(stolen.MarketValue - 1500f) < 0.5f, "stolen crate market value should be 1500, got " + stolen.MarketValue);
        }

        private static void DestroyedRackLeavesCrates(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var rack = Rack(s, room.Min);
            var crate = s.SpawnCrate(rack.Position, Owner, CargoCustody.Depositor, 500m, StorageKind.Rack, "GT-K");
            int before = s.World.State.Cargo.Count;

            rack.Destroy(DestroyMode.KillFinalize);

            Check(crate.Spawned && !crate.Destroyed, "crate stays on the map");
            Equal(before, s.World.State.Cargo.Count, "ledger crate count");
            Check(!StorageAdapter.IsValidlyStored(crate, CargoAdapter.Comp(crate).Record), "crate no longer earns fees");
        }

        private static void ContractCratesAreNotCaravanCandidates(GameTestScope s)
        {
            Check(HarmonyGate.Active, "Harmony patches are active");
            var area = s.FindClearRect(2);
            var crate = s.SpawnCrate(area.Min, Owner, CargoCustody.Depositor, 500m, StorageKind.Rack, "GT-K");
            s.Map.areaManager.Home[area.Min] = true;

            var items = CaravanFormingUtility.AllReachableColonyItems(s.Map, allowEvenIfOutsideHomeArea: true);
            Check(!items.Contains(crate), "contract crate must not be offered to caravans");
        }
    }
}
