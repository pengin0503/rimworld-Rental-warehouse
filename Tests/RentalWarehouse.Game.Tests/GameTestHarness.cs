using System;
using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse.Game.Tests
{
    public sealed class GameTestFailure : Exception
    {
        public GameTestFailure(string message) : base(message)
        {
        }
    }

    /// <summary>Minimal runner for in-game cases. Each case cleans up what it spawned, pass or fail.</summary>
    public static class GameTestHarness
    {
        public static void Run(string suite, IEnumerable<(string Name, Action<GameTestScope> Body)> cases)
        {
            int pass = 0, fail = 0;
            foreach (var (name, body) in cases)
            {
                var scope = new GameTestScope(Find.CurrentMap);
                try
                {
                    body(scope);
                    pass++;
                    Log.Message("[RW GameTest] PASS " + suite + "." + name);
                }
                catch (Exception e)
                {
                    fail++;
                    Log.Error("[RW GameTest] FAIL " + suite + "." + name + ": " + e.Message + "\n" + e.StackTrace);
                }
                finally
                {
                    scope.Dispose();
                }
            }
            string summary = "[RW GameTest] " + suite + ": " + pass + " passed, " + fail + " failed";
            if (fail == 0) Log.Message(summary);
            else Log.Error(summary);
        }

        public static void Check(bool condition, string message)
        {
            if (!condition) throw new GameTestFailure(message);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new GameTestFailure(what + ": expected " + expected + ", got " + actual);
        }
    }

    /// <summary>Tracks Things and ledger entries a case creates so they can be removed afterwards.</summary>
    public sealed class GameTestScope : IDisposable
    {
        private readonly List<Thing> spawned = new List<Thing>();
        private readonly List<string> cargoIds = new List<string>();
        private readonly List<string> contractIds = new List<string>();

        public GameTestScope(Map map)
        {
            Map = map;
        }

        public Map Map { get; }
        public WarehouseWorldComponent World => WarehouseWorldComponent.Instance;

        public T Spawn<T>(T thing, IntVec3 cell, Rot4? rot = null) where T : Thing
        {
            var result = (T)GenSpawn.Spawn(thing, cell, Map, rot ?? Rot4.North);
            spawned.Add(result);
            return result;
        }

        public Thing SpawnBuilding(ThingDef def, IntVec3 cell, ThingDef stuff = null, Rot4? rot = null)
        {
            var thing = ThingMaker.MakeThing(def, stuff);
            thing.SetFaction(Faction.OfPlayer);
            return Spawn(thing, cell, rot);
        }

        /// <summary>Registers a crate record in the ledger and spawns its Thing.</summary>
        public Thing SpawnCrate(IntVec3 cell, string ownerFactionId, CargoCustody custody, decimal value, StorageKind required, string contractId = null)
        {
            string id = "GT-" + Rand.Int.ToString("X8");
            var record = new CargoRecord
            {
                CargoId = id,
                ContractId = contractId,
                OwnerFactionId = ownerFactionId,
                Custody = custody,
                DeclaredValue = value,
                MassKg = 10m,
                RequiredStorage = required,
                Manifest = { new ManifestEntry { ThingDefName = "Steel", Count = 20 } },
            };
            World.State.Cargo[id] = record;
            cargoIds.Add(id);
            return Spawn(CargoAdapter.MakeCrate(record), cell);
        }

        public void TrackContract(string contractId) => contractIds.Add(contractId);

        /// <summary>Finds a clear, unfogged, standable square of the given size near the map centre.</summary>
        public CellRect FindClearRect(int size)
        {
            foreach (var c in GenRadial.RadialCellsAround(Map.Center, 40f, useCenter: true))
            {
                var rect = new CellRect(c.x, c.z, size, size);
                if (!rect.InBounds(Map)) continue;
                bool ok = true;
                foreach (var cell in rect)
                {
                    if (!cell.Standable(Map) || cell.Fogged(Map) || cell.GetEdifice(Map) != null || cell.GetFirstItem(Map) != null || cell.GetFirstPawn(Map) != null)
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok) return rect;
            }
            throw new GameTestFailure("no clear " + size + "x" + size + " area near the map centre");
        }

        /// <summary>Builds a closed, roofed room and returns its interior.</summary>
        public CellRect BuildRoom(int interior)
        {
            var outer = FindClearRect(interior + 2);
            foreach (var cell in outer.EdgeCells) SpawnBuilding(ThingDefOf.Wall, cell, ThingDefOf.Steel);
            var inner = outer.ContractedBy(1);
            foreach (var cell in outer) Map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
            return inner;
        }

        public void Dispose()
        {
            for (int i = spawned.Count - 1; i >= 0; i--)
            {
                var t = spawned[i];
                if (t.Destroyed) continue;
                if (t.def == RWDefOf.RW_SealedCargo) CargoAdapter.RemoveCrate(t);
                else t.Destroy(DestroyMode.Vanish);
            }
            if (World == null) return;
            foreach (var id in cargoIds) World.State.Cargo.Remove(id);
            foreach (var id in contractIds) World.State.Contracts.RemoveAll(c => c.Terms.ContractId == id);
        }
    }
}
