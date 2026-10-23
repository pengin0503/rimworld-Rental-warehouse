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
    /// <summary>A07/A09 placement and lifetime parts. Map generation itself needs a caravan visit (manual step).</summary>
    public static class SiteGenerationCases
    {
        [DebugAction("Rental Warehouse tests", "Site generation cases (A07/A09)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("SiteGeneration", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("EligibleTilesAvoidAllWorldObjects", EligibleTilesAvoidWorldObjects);
            yield return ("CreateSitePlacesOneWorldObject", CreateSiteOnce);
        }

        private static void EligibleTilesAvoidWorldObjects(GameTestScope s)
        {
            var tiles = VaultDiscovery.EligibleTiles(s.World.State);
            Check(tiles.Count > 0, "some eligible tile exists");
            foreach (var t in tiles)
            {
                Check(!Find.WorldObjects.AnyWorldObjectAt(new PlanetTile(t)), "tile " + t + " is free of world objects (no existing settlement)");
            }
        }

        private static void CreateSiteOnce(GameTestScope s)
        {
            var state = s.World.State;
            var ctx = VaultDiscovery.BuildContext(s.World, Ids.Now, needTiles: true);
            var faction = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer));
            var record = SiteService.Create(state, "GT-Site", 42, SiteTier.Small, Ids.Of(faction), ctx.EligibleTiles[0], ctx);
            Check(record != null, "site record created");
            state.Sites.Add(record);
            var effect = new EffectRequest { EffectId = "GT-Site:create", Kind = EffectKind.CreateSite, EntityId = "GT-Site" };
            try
            {
                EffectDispatcher.Apply(s.World, effect);
                EffectDispatcher.Apply(s.World, effect);
                Equal(1, Find.WorldObjects.AllWorldObjects.OfType<VaultSite>().Count(v => v.siteId == "GT-Site"), "world objects for the site");
            }
            finally
            {
                VaultDiscovery.FindSite("GT-Site")?.Destroy();
                state.Sites.Remove(record);
                foreach (var id in record.CargoIds) state.Cargo.Remove(id);
            }
        }
    }
}
