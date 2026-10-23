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
    /// <summary>A12/A13 parts: one-time initialisation, snapshot contents, duplicate crate isolation, ledger round trip.</summary>
    public static class MigrationCases
    {
        [DebugAction("Rental Warehouse tests", "Migration/lifecycle cases (A12/A13)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Migration", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("InitializationHappenedExactlyOnce", InitializedOnce);
            yield return ("SnapshotSeesCurrentMapAndFactions", SnapshotContents);
            yield return ("DuplicateCrateIsDetached", DuplicateCrate);
            yield return ("LedgerRoundTripsThroughCodec", LedgerRoundTrip);
        }

        private static void InitializedOnce(GameTestScope s)
        {
            Check(s.World.State.Initialized, "ledger initialised on load");
            long installed = s.World.State.InstalledTick ?? -1;
            Check(!LifecycleService.Initialize(s.World.State, 1, Ids.Now), "second initialisation is a no-op");
            Equal(installed, s.World.State.InstalledTick ?? -1, "install tick unchanged");
        }

        private static void SnapshotContents(GameTestScope s)
        {
            var snap = LifecycleAdapter.Snapshot(s.World);
            Check(snap.MapIds.Contains(Ids.Of(s.Map)), "current map listed");
            Check(snap.FactionIds.Count > 0, "factions listed");
            Check(snap.DefNames.Contains("Steel"), "defs listed");
            Check(!snap.FactionIds.Contains(CargoService.PlayerFactionId), "player faction excluded");
        }

        private static void DuplicateCrate(GameTestScope s)
        {
            var area = s.FindClearRect(2);
            var first = s.SpawnCrate(area.Min, "Faction_GT", CargoCustody.Depositor, 100m, StorageKind.Rack);
            string id = CargoAdapter.Comp(first).cargoId;
            var twin = CargoAdapter.MakeCrate(s.World.State.Cargo[id]);
            s.Spawn(twin, area.Max);
            Check(CargoAdapter.Comp(twin).cargoId == null, "twin detached from the ledger");
            Check(s.World.FindCrate(id) == first, "original stays registered");
            Equal(0f, twin.MarketValue, "twin has no value");
        }

        private static void LedgerRoundTrip(GameTestScope s)
        {
            var saved = StateCodec.Export(s.World.State);
            var loaded = StateCodec.Import(Migration.Upgrade(saved));
            Equal(saved.Payload, StateCodec.Export(loaded).Payload, "payload after round trip");
        }
    }
}
