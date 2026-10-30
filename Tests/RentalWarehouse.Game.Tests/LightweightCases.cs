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
    /// <summary>The lookups and caches that keep the per-tick and per-frame work small must still return what the slow paths did.</summary>
    public static class LightweightCases
    {
        [DebugAction("Rental Warehouse tests", "Lightweight cases (A24)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Lightweight", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("MapAndFactionIdsRoundTrip", IdsRoundTrip);
            yield return ("UnknownOrMalformedIdsGiveNull", MalformedIds);
            yield return ("InstanceIsTheCurrentWorldsComponent", InstanceMatches);
            yield return ("FacilitiesAreFoundThroughTheirDefs", FacilitiesByDef);
            yield return ("ContractLookupFindsByIdOrReturnsNull", ContractLookup);
        }

        private static void IdsRoundTrip(GameTestScope s)
        {
            foreach (var map in Find.Maps) Check(Ids.Map(Ids.Of(map)) == map, "map " + map.uniqueID);
            foreach (var faction in Find.FactionManager.AllFactionsListForReading)
            {
                Check(Ids.Faction(Ids.Of(faction)) == faction, "faction " + faction.Name);
                Equal(faction.GetUniqueLoadID(), faction.IsPlayer ? CargoService.PlayerFactionId : Ids.Of(faction), "faction id text");
            }
            Check(Ids.Of(s.Map) == Ids.Of(s.Map), "the same map keeps one id string");
        }

        private static void MalformedIds(GameTestScope s)
        {
            Check(Ids.Map(null) == null && Ids.Map("") == null && Ids.Map("Map_") == null && Ids.Map("Map_x") == null, "malformed map ids");
            Check(Ids.Map("Map_99999999") == null, "a map that does not exist");
            Check(Ids.Faction(null) == null && Ids.Faction("Faction_") == null && Ids.Faction("Faction_99999999") == null, "unknown faction ids");
        }

        private static void InstanceMatches(GameTestScope s)
        {
            Check(WarehouseWorldComponent.Instance == Find.World.GetComponent<WarehouseWorldComponent>(), "cached instance is the world's component");
        }

        private static void FacilitiesByDef(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var rack = s.SpawnBuilding(RWDefOf.RW_StorageRack, room.Min);
            var vault = s.SpawnBuilding(RWDefOf.RW_Vault, room.Min + new IntVec3(2, 0, 0));
            var found = StorageAdapter.Facilities(s.Map).ToList();
            Check(found.Contains((Building_WarehouseStorage)rack) && found.Contains((Building_WarehouseStorage)vault), "rack and vault are listed");
            var (rackCapacity, vaultCapacity) = StorageAdapter.BestCapacity();
            Check(rackCapacity >= vaultCapacity && vaultCapacity >= Building_WarehouseStorage.CapacityCrates, "capacity counts the vault");
        }

        private static void ContractLookup(GameTestScope s)
        {
            var terms = DeliveryCases.Offer(s, ContractKind.Normal, 500m);
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            var c = new ContractState { Terms = terms, Phase = ContractPhase.Active, MapId = Ids.Of(s.Map) };
            s.World.State.Contracts.Add(c);
            Check(s.World.State.Contract(terms.ContractId) == c, "found by id");
            Check(s.World.State.Contract("no-such-contract") == null, "unknown id");
        }
    }
}
