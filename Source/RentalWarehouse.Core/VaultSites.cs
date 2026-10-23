using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    public enum SiteTier { Small = 1, Medium = 2, Large = 3 }

    public sealed class VaultState
    {
        public string VaultId = "";
        public decimal Progress;
        public long RequiredWork;
        public List<string> CargoIds = new List<string>();
        public bool Unlocked;
        public bool Destroyed;
        public bool DestructionRolled;
    }

    public sealed class SiteRecord
    {
        public string SiteId = "";
        public string FactionId = "";
        public SiteTier Tier;
        public int Seed;
        public int Tile;
        public List<string> CargoIds = new List<string>();
        public decimal GuardPoints;
        public decimal GuardPointsRemaining;
        public long CreatedTick;
        public long? FirstEntryTick;
        public long? AlarmTick;
        public bool ReinforcementApplied;
        public List<VaultState> Vaults = new List<VaultState>();
        public bool Visited;
        public bool Closed;
    }

    public sealed class SiteContext
    {
        public long NowTick;
        public bool PlayerPresent;
        public bool PlayerCaravanEnRoute;
        public bool Loaded;
        public List<int> EligibleTiles = new List<int>();
        public List<FactionInfo> Factions = new List<FactionInfo>();
        public IList<Good> Goods = new List<Good>();
        public decimal ThreatPoints;
        public Dictionary<string, decimal> MinimumGroupPoints = new Dictionary<string, decimal>();
    }

    /// <summary>
    /// Independent NPC vault sites (spec 7): discovered on a schedule, inventory and guards fixed at creation, never
    /// restocked. Revisits rebuild the map from this record, so only what is still there comes back.
    /// </summary>
    public static class SiteService
    {
        public static SiteRecord? Create(WorldState world, string siteId, int seed, SiteTier tier, string factionId, int tile, SiteContext ctx)
        {
            decimal guard = ctx.ThreatPoints * VaultSiteParameters.GuardFactor(tier);
            ctx.MinimumGroupPoints.TryGetValue(factionId, out decimal minimum);
            if (guard <= 0m || guard < minimum) return null;

            var rng = new DeterministicRandom(DeterministicRandom.Mix(seed, "site-inventory"));
            int crates = VaultSiteParameters.Crates(tier);
            decimal total = rng.Range(VaultSiteParameters.MinValue(tier) / ManifestBuilder.MinFillRatio, VaultSiteParameters.MaxValue(tier));
            var targets = new List<decimal>();
            for (int i = 0; i < crates; i++) targets.Add(decimal.Round(total / crates, 2));
            var manifests = ManifestBuilder.Build(ctx.Goods, targets, Limits.MaxCrateMassKg - Limits.CrateTareKg, rng);
            if (manifests == null) return null;

            var site = new SiteRecord
            {
                SiteId = siteId,
                FactionId = factionId,
                Tier = tier,
                Seed = seed,
                Tile = tile,
                GuardPoints = guard,
                GuardPointsRemaining = guard,
                CreatedTick = ctx.NowTick,
            };
            int vaultCount = (crates + VaultSiteParameters.CratesPerVault - 1) / VaultSiteParameters.CratesPerVault;
            for (int v = 0; v < vaultCount; v++)
            {
                site.Vaults.Add(new VaultState { VaultId = siteId + "-V" + v.ToString(CultureInfo.InvariantCulture), RequiredWork = VaultSiteParameters.UnlockWork(tier) });
            }
            for (int i = 0; i < crates; i++)
            {
                string cargoId = siteId + "-" + i.ToString(CultureInfo.InvariantCulture);
                var vault = site.Vaults[i * vaultCount / crates];
                var (manifest, value, mass) = manifests[i];
                world.Cargo[cargoId] = new CargoRecord
                {
                    CargoId = cargoId,
                    SiteId = siteId,
                    OwnerFactionId = factionId,
                    Custody = CargoCustody.Depositor,
                    Manifest = manifest,
                    DeclaredValue = value,
                    MassKg = mass + Limits.CrateTareKg,
                    RequiredStorage = StorageKind.Vault,
                    ContainerId = vault.VaultId,
                };
                site.CargoIds.Add(cargoId);
                vault.CargoIds.Add(cargoId);
            }
            return site;
        }

        public static Decision<List<SiteRecord>> Discover(WorldState world, SiteContext ctx, long now)
        {
            var created = new List<SiteRecord>();
            if (world.BusinessStartTick == null || world.NextSiteTick == null || now < world.NextSiteTick.Value) return Decision<List<SiteRecord>>.Ok(created);

            long due = world.NextSiteTick.Value;
            long index = (due - world.BusinessStartTick.Value - VaultSiteParameters.FirstDiscoveryDelay) / VaultSiteParameters.DiscoveryInterval;
            while (world.NextSiteTick.Value <= now) world.NextSiteTick += VaultSiteParameters.DiscoveryInterval;
            if (!world.BusinessOpen || !world.Settings.VaultSitesEnabled) return Decision<List<SiteRecord>>.Ok(created);

            int unvisited = 0;
            var usedTiles = new HashSet<int>();
            foreach (var s in world.Sites)
            {
                if (s.Closed) continue;
                usedTiles.Add(s.Tile);
                if (!s.Visited) unvisited++;
            }
            if (unvisited >= VaultSiteParameters.MaxUnvisited) return Decision<List<SiteRecord>>.Ok(created);

            var rng = new DeterministicRandom(DeterministicRandom.Mix(world.WorldSeed, "site:" + index.ToString(CultureInfo.InvariantCulture)));
            var hostile = new List<FactionInfo>();
            var friendly = new List<FactionInfo>();
            foreach (var f in ctx.Factions)
            {
                if (!f.Humanlike || f.Defeated || f.FactionId == CargoService.PlayerFactionId) continue;
                (f.HostileToPlayer ? hostile : friendly).Add(f);
            }
            var pool = hostile.Count > 0 ? hostile : world.Settings.NonHostileVaultSitesEnabled ? friendly : hostile;
            if (pool.Count == 0) return Decision<List<SiteRecord>>.Fail("RW_Site_NoFaction", created);
            var faction = pool[rng.Next(pool.Count)];

            var tiles = ctx.EligibleTiles.FindAll(t => !usedTiles.Contains(t));
            if (tiles.Count == 0) return Decision<List<SiteRecord>>.Fail("RW_Site_NoTile", created);
            int tile = tiles[rng.Next(tiles.Count)];

            int seed = rng.Next(int.MaxValue);
            int firstTier = rng.Range(1, 3);
            string siteId = "RWS-" + index.ToString(CultureInfo.InvariantCulture);
            for (int k = 0; k < 3; k++)
            {
                var tier = (SiteTier)((firstTier - 1 + k) % 3 + 1);
                var site = Create(world, siteId, seed, tier, faction.FactionId, tile, ctx);
                if (site == null) continue;
                world.Sites.Add(site);
                created.Add(site);
                var effect = new EffectRequest { EffectId = siteId + ":create", Kind = EffectKind.CreateSite, EntityId = siteId };
                return Decision<List<SiteRecord>>.Ok(created, new List<EffectRequest> { effect });
            }
            return Decision<List<SiteRecord>>.Fail("RW_Site_NoGuards", created);
        }

        public static bool ShouldClose(SiteRecord site, SiteContext ctx)
        {
            if (site.Closed) return false;
            if (!site.Visited || site.FirstEntryTick == null)
            {
                return ctx.NowTick >= site.CreatedTick + VaultSiteParameters.UnvisitedLifetime && !ctx.PlayerCaravanEnRoute && !ctx.PlayerPresent;
            }
            return ctx.NowTick >= site.FirstEntryTick.Value + VaultSiteParameters.WithdrawalAfterFirstEntry && !ctx.PlayerPresent;
        }

        /// <summary>Spec 7: remaining goods count as collected; crates the player already took stay theirs.</summary>
        public static Decision<SiteRecord> Close(WorldState world, SiteRecord site, long now)
        {
            if (site.Closed) return Decision<SiteRecord>.Fail("RW_Reject_SiteClosed", site);
            foreach (var id in new List<string>(CratesAtSite(world, site))) world.Cargo.Remove(id);
            site.Closed = true;
            var effect = new EffectRequest { EffectId = site.SiteId + ":close", Kind = EffectKind.CloseSite, EntityId = site.SiteId };
            return Decision<SiteRecord>.Ok(site, new List<EffectRequest> { effect });
        }

        public static void Enter(SiteRecord site, long now)
        {
            site.Visited = true;
            if (site.FirstEntryTick == null) site.FirstEntryTick = now;
        }

        /// <summary>Crates still belonging to the site's owner (not taken, not destroyed).</summary>
        public static IEnumerable<string> CratesAtSite(WorldState world, SiteRecord site)
        {
            foreach (var id in site.CargoIds)
            {
                if (world.Cargo.TryGetValue(id, out var c) && c.SiteId == site.SiteId && c.Custody == CargoCustody.Depositor) yield return id;
            }
        }

        public static SiteRecord? Site(WorldState world, string siteId) => world.Sites.Find(s => s.SiteId == siteId);
    }
}
