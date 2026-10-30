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
        public long? ClosedTick;
        public bool HostilityDeclared;
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

        /// <summary>Gap between intel report <paramref name="index"/> and the next one (spec 7: 3–12 days).</summary>
        public static long DiscoveryInterval(int worldSeed, int index)
        {
            var rng = new DeterministicRandom(DeterministicRandom.Mix(worldSeed, "site-interval:" + index.ToString(CultureInfo.InvariantCulture)));
            return rng.Range(VaultSiteParameters.MinDiscoveryInterval, VaultSiteParameters.MaxDiscoveryInterval);
        }

        public static Decision<List<SiteRecord>> Discover(WorldState world, SiteContext ctx, long now)
        {
            var created = new List<SiteRecord>();
            if (world.BusinessStartTick == null || world.NextSiteTick == null || now < world.NextSiteTick.Value) return Decision<List<SiteRecord>>.Ok(created);

            int index = world.NextSiteIndex;
            while (world.NextSiteTick.Value <= now)
            {
                world.NextSiteTick += DiscoveryInterval(world.WorldSeed, world.NextSiteIndex);
                world.NextSiteIndex++;
            }
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
            site.ClosedTick = now;
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

    /// <summary>
    /// Breaking into NPC vaults (spec 7): per-vault unlock progress at speed 1 + 0.05 × crafting; alarm on first work or
    /// damage, one reinforcement of half the initial guard points 0.25 days later (applied on the next load if the map
    /// was not loaded); forced opening rolls a fixed-seed 30% loss once per crate.
    /// </summary>
    public static class VaultService
    {
        public static decimal WorkSpeed(int craftingLevel) => 1m + VaultSiteParameters.CraftingSpeedPerLevel * System.Math.Max(0, craftingLevel);

        private static VaultState? Vault(SiteRecord site, string vaultId) => site.Vaults.Find(v => v.VaultId == vaultId);

        public static Decision<SiteRecord> Work(SiteRecord site, string vaultId, int craftingLevel, long ticks, long now)
        {
            var vault = Vault(site, vaultId);
            if (vault == null || site.Closed) return Decision<SiteRecord>.Fail("RW_Reject_UnknownVault", site);
            if (vault.Unlocked || vault.Destroyed) return Decision<SiteRecord>.Fail("RW_Reject_VaultOpen", site);
            if (ticks <= 0) return Decision<SiteRecord>.Ok(site);
            if (site.AlarmTick == null) site.AlarmTick = now;
            vault.Progress += ticks * WorkSpeed(craftingLevel);
            var effects = new List<EffectRequest>();
            if (vault.Progress >= vault.RequiredWork)
            {
                vault.Progress = vault.RequiredWork;
                vault.Unlocked = true;
                effects.Add(Release(site, vault));
            }
            return Decision<SiteRecord>.Ok(site, effects);
        }

        public static decimal Fraction(VaultState vault) => vault.RequiredWork <= 0 ? 1m : System.Math.Min(1m, vault.Progress / vault.RequiredWork);

        private static EffectRequest Release(SiteRecord site, VaultState vault) => new EffectRequest
        {
            EffectId = vault.VaultId + ":release",
            Kind = EffectKind.ReleaseVaultCargo,
            EntityId = vault.VaultId,
            Payload = { ["site"] = site.SiteId },
        };

        public static Decision<SiteRecord> ForceOpen(WorldState world, SiteRecord site, string vaultId, long now)
        {
            var vault = Vault(site, vaultId);
            if (vault == null) return Decision<SiteRecord>.Fail("RW_Reject_UnknownVault", site);
            if (vault.Destroyed) return Decision<SiteRecord>.Fail("RW_Reject_VaultOpen", site);
            NotifyDamaged(site, now);
            vault.Destroyed = true;
            var effects = new List<EffectRequest>();
            if (!vault.DestructionRolled && !vault.Unlocked)
            {
                foreach (var id in vault.CargoIds)
                {
                    if (!world.Cargo.TryGetValue(id, out var c) || c.Custody != CargoCustody.Depositor) continue;
                    var rng = new DeterministicRandom(DeterministicRandom.Mix(site.Seed, "force:" + id));
                    if (rng.Chance(VaultSiteParameters.ForcedOpenLossChance)) c.Custody = CargoCustody.Destroyed;
                }
            }
            vault.DestructionRolled = true;
            if (!vault.Unlocked) effects.Add(Release(site, vault));
            return Decision<SiteRecord>.Ok(site, effects);
        }

        public static void NotifyDamaged(SiteRecord site, long now)
        {
            if (site.AlarmTick == null) site.AlarmTick = now;
        }

        public static Decision<SiteRecord> Alarm(SiteRecord site, long now, bool loaded)
        {
            var effects = new List<EffectRequest>();
            if (site.AlarmTick == null || site.ReinforcementApplied || site.Closed) return Decision<SiteRecord>.Ok(site, effects);
            if (now < site.AlarmTick.Value + VaultSiteParameters.AlarmDelay || !loaded) return Decision<SiteRecord>.Ok(site, effects);
            site.ReinforcementApplied = true;
            effects.Add(new EffectRequest
            {
                EffectId = site.SiteId + ":reinforcement",
                Kind = EffectKind.SpawnReinforcement,
                EntityId = site.SiteId,
                Amount = site.GuardPoints * VaultSiteParameters.ReinforcementFraction,
            });
            return Decision<SiteRecord>.Ok(site, effects);
        }

        public static Decision<SiteRecord> Take(WorldState world, SiteRecord site, string cargoId)
        {
            if (!world.Cargo.TryGetValue(cargoId, out var c) || c.SiteId != site.SiteId) return Decision<SiteRecord>.Fail("RW_Reject_UnknownCargo", site);
            var vault = c.ContainerId == null ? null : Vault(site, c.ContainerId);
            if (vault != null && !vault.Unlocked && !vault.Destroyed) return Decision<SiteRecord>.Fail("RW_Reject_VaultLocked", site);
            var taken = CargoService.TakeFromSite(c);
            return taken.Success ? Decision<SiteRecord>.Ok(site) : Decision<SiteRecord>.Fail(taken.ReasonKey!, site);
        }

        /// <summary>Goodwill change that makes a non-hostile owner hostile; never deducts twice (spec 7).</summary>
        public static int HostilityChange(int currentGoodwill, bool alreadyHostile, int threshold)
        {
            if (alreadyHostile || currentGoodwill <= threshold) return 0;
            return threshold - currentGoodwill;
        }

        public static Decision<SiteRecord> DeclareAttack(SiteRecord site, int currentGoodwill, bool alreadyHostile, int threshold)
        {
            var effects = new List<EffectRequest>();
            if (site.HostilityDeclared) return Decision<SiteRecord>.Ok(site, effects);
            site.HostilityDeclared = true;
            int change = HostilityChange(currentGoodwill, alreadyHostile, threshold);
            if (change != 0)
            {
                effects.Add(new EffectRequest
                {
                    EffectId = site.SiteId + ":hostility",
                    Kind = EffectKind.ApplyGoodwill,
                    EntityId = site.FactionId,
                    Amount = change,
                    Payload = { ["site"] = site.SiteId },
                });
            }
            return Decision<SiteRecord>.Ok(site, effects);
        }
    }
}
