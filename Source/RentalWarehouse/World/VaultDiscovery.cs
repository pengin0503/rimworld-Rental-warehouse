using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>World-side schedule of vault intel, site creation and closure (spec 7).</summary>
    public static class VaultDiscovery
    {
        public const int TileCandidates = 12;

        public static WorldObjectDef SiteDef => DefDatabase<WorldObjectDef>.GetNamed("RW_VaultSite");

        public static void Tick(WarehouseWorldComponent world, long now)
        {
            var state = world.State;
            if (state.NextSiteTick != null && now >= state.NextSiteTick.Value)
            {
                var d = SiteService.Discover(state, BuildContext(world, now, needTiles: true), now);
                if (!d.Success && d.ReasonKey != null && state.BusinessOpen && state.Settings.VaultSitesEnabled)
                {
                    Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                }
                world.Enqueue(d.Effects, applyNow: false);
            }

            foreach (var record in state.Sites)
            {
                if (record.Closed) continue;
                var site = FindSite(record.SiteId);
                var ctx = new SiteContext
                {
                    NowTick = now,
                    PlayerPresent = PlayerPresent(site, record),
                    PlayerCaravanEnRoute = CaravanEnRoute(record),
                    Loaded = site?.HasMap ?? false,
                };
                CloseIfDue(world, record, ctx);
            }
        }

        /// <summary>Before deleting site cargo, reconcile physical possession in this same call, not a later tick.</summary>
        public static bool CloseIfDue(WarehouseWorldComponent world, SiteRecord record, SiteContext context)
        {
            if (!SiteService.ShouldClose(record, context)) return false;
            VaultLootAdapter.Tick(world, context.NowTick);
            world.Enqueue(SiteService.Close(world.State, record, context.NowTick).Effects, applyNow: false);
            return true;
        }

        public static SiteContext BuildContext(WarehouseWorldComponent world, long now, bool needTiles)
        {
            var offer = OfferGenerator.BuildContext();
            var ctx = new SiteContext { NowTick = now, Goods = offer.Goods, Factions = offer.Factions };
            var home = Find.AnyPlayerHomeMap;
            ctx.ThreatPoints = home == null ? 0m : (decimal)StorytellerUtility.DefaultThreatPointsNow(home);
            foreach (var f in Find.FactionManager.AllFactionsListForReading)
            {
                if (!f.IsPlayer && f.def.humanlikeFaction) ctx.MinimumGroupPoints[Ids.Of(f)] = (decimal)f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat);
            }
            if (needTiles) ctx.EligibleTiles = EligibleTiles(world.State);
            return ctx;
        }

        /// <summary>Reachable land tiles near the colony without any world object (so never an existing settlement).</summary>
        public static List<int> EligibleTiles(WorldState state)
        {
            var tiles = new HashSet<int>();
            var used = new HashSet<int>(state.Sites.Where(s => !s.Closed).Select(s => s.Tile));
            for (int i = 0; i < TileCandidates * 3 && tiles.Count < TileCandidates; i++)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, 4, 20, allowCaravans: false)) continue;
                int id = tile.tileId;
                if (used.Contains(id) || Find.WorldObjects.AnyWorldObjectAt(tile)) continue;
                tiles.Add(id);
            }
            return tiles.OrderBy(t => t).ToList();
        }

        public static VaultSite FindSite(string siteId)
        {
            foreach (var wo in Find.WorldObjects.AllWorldObjects)
            {
                if (wo is VaultSite s && s.siteId == siteId) return s;
            }
            return null;
        }

        public static bool PlayerPresent(VaultSite site, SiteRecord record)
        {
            if (site != null && site.HasMap && site.Map.mapPawns.AnyPawnBlockingMapRemoval) return true;
            foreach (var c in Find.WorldObjects.Caravans)
            {
                if (c.IsPlayerControlled && c.Tile.tileId == record.Tile) return true;
            }
            return false;
        }

        public static bool CaravanEnRoute(SiteRecord record)
        {
            foreach (var c in Find.WorldObjects.Caravans)
            {
                if (c.IsPlayerControlled && c.pather != null && c.pather.Moving && c.pather.Destination.tileId == record.Tile) return true;
            }
            return false;
        }

        /// <summary>CreateSite effect: places the world object (reconciles by site ID first).</summary>
        public static bool CreateSite(WarehouseWorldComponent world, EffectRequest effect)
        {
            var record = SiteService.Site(world.State, effect.EntityId);
            if (record == null || record.Closed) return true;
            if (FindSite(record.SiteId) != null) return true;
            var faction = Ids.Faction(record.FactionId);
            if (faction == null) return true;
            var site = (VaultSite)WorldObjectMaker.MakeWorldObject(SiteDef);
            site.siteId = record.SiteId;
            site.Tile = new PlanetTile(record.Tile);
            site.SetFaction(faction);
            Find.WorldObjects.Add(site);
            Find.LetterStack.ReceiveLetter("RW_Letter_SiteFoundTitle".Translate(), "RW_Letter_SiteFound".Translate(VaultInfoView.Describe(world, record)),
                LetterDefOf.NeutralEvent, site);
            return true;
        }

        /// <summary>CloseSite effect: removes the world object now, or lets the map removal take it when a map is loaded.</summary>
        public static bool CloseSite(WarehouseWorldComponent world, EffectRequest effect)
        {
            var site = FindSite(effect.EntityId);
            if (site == null || site.Destroyed) return true;
            if (site.HasMap) return true;
            site.Destroy();
            return true;
        }
    }
}
