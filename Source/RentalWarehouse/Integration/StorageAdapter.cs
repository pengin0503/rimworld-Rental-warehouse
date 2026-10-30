using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Reads the player's racks and vaults into a <see cref="StorageSnapshot"/> for the Core.</summary>
    public static class StorageAdapter
    {
        public static IEnumerable<Building_WarehouseStorage> Facilities(Map map)
        {
            if (map == null) yield break;
            foreach (var b in map.listerBuildings.allBuildingsColonist)
            {
                if (b is Building_WarehouseStorage s && s.Faction == Faction.OfPlayer) yield return s;
            }
        }

        /// <summary>
        /// Crates the intact racks and vaults of the home map with the most room can hold: normal cargo may use either kind,
        /// high-risk cargo only vaults. Used to keep offers within what the colony can store.
        /// </summary>
        public static (int Rack, int Vault) BestCapacity()
        {
            int bestRack = 0, bestVault = 0;
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                int racks = 0, vaults = 0;
                foreach (var f in Facilities(map))
                {
                    if (!f.IsValidFacility) continue;
                    if (f.Kind == StorageKind.Vault) vaults += Building_WarehouseStorage.CapacityCrates;
                    else racks += Building_WarehouseStorage.CapacityCrates;
                }
                bestRack = System.Math.Max(bestRack, racks + vaults);
                bestVault = System.Math.Max(bestVault, vaults);
            }
            return (bestRack, bestVault);
        }

        public static StorageSnapshot Capture(string mapId)
        {
            var snapshot = new StorageSnapshot { MapId = mapId };
            var map = Ids.Map(mapId);
            if (map == null) return snapshot;
            foreach (var f in Facilities(map))
            {
                bool valid = f.IsValidFacility;
                snapshot.Facilities.Add(new FacilitySnapshot
                {
                    FacilityId = f.FacilityId,
                    Kind = f.Kind,
                    FreeSlots = valid ? System.Math.Max(0, Building_WarehouseStorage.CapacityCrates - f.StoredCount) : 0,
                    IsValid = valid,
                });
                if (!valid) continue;
                foreach (var crate in f.StoredCrates())
                {
                    var record = CargoAdapter.Comp(crate)?.Record;
                    if (record != null && f.Satisfies(record.RequiredStorage)) snapshot.ValidStoredCargoIds.Add(record.CargoId);
                }
            }
            return snapshot;
        }

        public static Building_WarehouseStorage FacilityAt(Thing crate)
        {
            if (crate == null || !crate.Spawned) return null;
            return crate.Position.GetFirstThing<Building_WarehouseStorage>(crate.Map);
        }

        public static bool IsValidlyStored(Thing crate, CargoRecord record) => FeeStopReason(crate, record) == null;

        /// <summary>Translation key explaining why this crate earns nothing right now, or null when it does.</summary>
        public static string FeeStopReason(Thing crate, CargoRecord record)
        {
            if (crate == null || !crate.Spawned) return "RW_FeeStop_NotInStorage";
            var facility = FacilityAt(crate);
            if (facility == null || facility.Faction != Faction.OfPlayer) return "RW_FeeStop_NotInStorage";
            if (!facility.Satisfies(record.RequiredStorage)) return "RW_FeeStop_WrongFacility";
            return facility.FacilityProblem();
        }
    }
}
