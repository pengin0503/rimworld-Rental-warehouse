using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class SiteTests
    {
        private static readonly List<Good> Goods = new List<Good>
        {
            new Good("Silver", null, 1m, 0.008m),
            new Good("Gold", null, 10m, 0.008m),
            new Good("ComponentIndustrial", null, 32m, 0.6m),
            new Good("Steel", null, 1.9m, 0.5m),
        };

        internal static SiteContext Ctx(long now, params int[] tiles) => new SiteContext
        {
            NowTick = now,
            EligibleTiles = tiles.Length == 0 ? Enumerable.Range(100, 20).ToList() : tiles.ToList(),
            Goods = Goods,
            ThreatPoints = 1000m,
            Factions =
            {
                new FactionInfo { FactionId = "Faction_1", Humanlike = true, HostileToPlayer = false },
                new FactionInfo { FactionId = "Faction_2", Humanlike = true, HostileToPlayer = true },
                new FactionInfo { FactionId = "Faction_9", Humanlike = false, HostileToPlayer = true },
            },
            MinimumGroupPoints = { ["Faction_1"] = 100m, ["Faction_2"] = 100m, ["Faction_9"] = 100m },
        };

        private static WorldState Open()
        {
            var world = new WorldState { WorldSeed = 99 };
            OfferService.SetBusinessOpen(world, true, 0);
            return world;
        }

        [Theory]
        [InlineData(SiteTier.Small, 2, 1000, 2000, 0.8, 6000, 1)]
        [InlineData(SiteTier.Medium, 4, 3000, 5000, 1.2, 12000, 1)]
        [InlineData(SiteTier.Large, 6, 6000, 9000, 1.6, 18000, 2)]
        public void Create_FixesInventoryAndGuardsByTier(SiteTier tier, int crates, int min, int max, double guard, int work, int vaults)
        {
            var world = new WorldState();

            var site = SiteService.Create(world, "Site-1", 1234, tier, "Faction_2", 105, Ctx(0))!;

            Assert.Equal(crates, site.CargoIds.Count);
            decimal total = site.CargoIds.Sum(id => world.Cargo[id].DeclaredValue);
            Assert.InRange(total, min, max);
            Assert.Equal(1000m * (decimal)guard, site.GuardPoints);
            Assert.Equal(site.GuardPoints, site.GuardPointsRemaining);
            Assert.Equal(vaults, site.Vaults.Count);
            Assert.All(site.Vaults, v => Assert.Equal(work, v.RequiredWork));
            Assert.All(site.Vaults, v => Assert.InRange(v.CargoIds.Count, 1, VaultSiteParameters.CratesPerVault));
            Assert.Equal(crates, site.Vaults.Sum(v => v.CargoIds.Count));
            Assert.All(site.CargoIds, id =>
            {
                var c = world.Cargo[id];
                Assert.Equal("Faction_2", c.OwnerFactionId);
                Assert.Equal("Site-1", c.SiteId);
                Assert.Null(c.ContractId);
                Assert.True(c.MassKg <= Limits.MaxCrateMassKg);
            });
        }

        [Fact]
        public void Create_SameSeed_SameInventory()
        {
            var a = new WorldState();
            var b = new WorldState();
            var sa = SiteService.Create(a, "S", 7, SiteTier.Medium, "Faction_2", 101, Ctx(0))!;
            var sb = SiteService.Create(b, "S", 7, SiteTier.Medium, "Faction_2", 101, Ctx(0))!;

            Assert.Equal(sa.CargoIds.Select(id => a.Cargo[id].DeclaredValue), sb.CargoIds.Select(id => b.Cargo[id].DeclaredValue));
        }

        [Fact]
        public void Create_TierWhoseGuardsCannotFormIsRejected()
        {
            var ctx = Ctx(0);
            ctx.MinimumGroupPoints["Faction_2"] = 900m;

            Assert.Null(SiteService.Create(new WorldState(), "S", 1, SiteTier.Small, "Faction_2", 101, ctx));
            Assert.NotNull(SiteService.Create(new WorldState(), "S", 1, SiteTier.Medium, "Faction_2", 101, ctx));
        }

        [Fact]
        public void Discovery_FirstAfterTwoDays_ThenEveryFive_MaxThreeUnvisited()
        {
            var world = Open();
            var created = new List<(long, SiteRecord)>();
            for (long t = 0; t <= 30 * D; t += CoreTime.CheckIntervalTicks)
            {
                foreach (var s in SiteService.Discover(world, Ctx(t), t).NewState) created.Add((t, s));
                Assert.True(world.Sites.Count(s => !s.Visited && !s.Closed) <= VaultSiteParameters.MaxUnvisited);
            }

            Assert.Equal(2 * D, created[0].Item1);
            Assert.Equal(7 * D, created[1].Item1);
            Assert.Equal(12 * D, created[2].Item1);
        }

        [Fact]
        public void Discovery_PrefersHostileFactions_NonHostileOffByDefault()
        {
            var world = Open();
            for (long t = 0; t <= 60 * D; t += CoreTime.CheckIntervalTicks)
            {
                SiteService.Discover(world, Ctx(t), t);
                foreach (var s in world.Sites.Where(s => !s.Closed && t >= s.CreatedTick + VaultSiteParameters.UnvisitedLifetime - 1)) s.Closed = true;
            }

            Assert.NotEmpty(world.Sites);
            Assert.All(world.Sites, s => Assert.Equal("Faction_2", s.FactionId));
        }

        [Fact]
        public void Discovery_NonHostileSetting_AllowsFriendlyFactions()
        {
            var world = Open();
            world.Settings.NonHostileVaultSitesEnabled = true;
            var ctx = Ctx(2 * D);
            ctx.Factions.RemoveAll(f => f.FactionId == "Faction_2");

            var d = SiteService.Discover(world, ctx, 2 * D);

            Assert.Equal("Faction_1", Assert.Single(d.NewState).FactionId);
        }

        [Fact]
        public void Discovery_UsesOnlyEligibleTiles_AndNeverTheSameTileTwice()
        {
            var world = Open();
            for (long t = 0; t <= 20 * D; t += CoreTime.CheckIntervalTicks) SiteService.Discover(world, Ctx(t, 501, 502, 503), t);

            Assert.All(world.Sites, s => Assert.Contains(s.Tile, new[] { 501, 502, 503 }));
            Assert.Equal(world.Sites.Count, world.Sites.Select(s => s.Tile).Distinct().Count());
        }

        [Fact]
        public void Discovery_NoTile_CreatesNothingAndReportsReason()
        {
            var world = Open();
            var ctx = Ctx(2 * D);
            ctx.EligibleTiles.Clear();

            var d = SiteService.Discover(world, ctx, 2 * D);

            Assert.Empty(d.NewState);
            Assert.Equal("RW_Site_NoTile", d.ReasonKey);
        }

        [Fact]
        public void Discovery_SitesDisabled_CreatesNothing()
        {
            var world = Open();
            world.Settings.VaultSitesEnabled = false;

            for (long t = 0; t <= 20 * D; t += CoreTime.CheckIntervalTicks) SiteService.Discover(world, Ctx(t), t);

            Assert.Empty(world.Sites);
        }

        [Fact]
        public void Unvisited_ExpiresAfterFifteenDays_UnlessCaravanEnRoute()
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", 1, SiteTier.Small, "Faction_2", 101, Ctx(0))!;
            var ctx = Ctx(VaultSiteParameters.UnvisitedLifetime);

            ctx.PlayerCaravanEnRoute = true;
            Assert.False(SiteService.ShouldClose(site, ctx));
            ctx.PlayerCaravanEnRoute = false;
            Assert.True(SiteService.ShouldClose(site, ctx));
            ctx.NowTick -= 1;
            Assert.False(SiteService.ShouldClose(site, ctx));
        }

        [Fact]
        public void Visited_ClosesFiveDaysAfterFirstEntry_OnlyWhenNobodyIsThere()
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", 1, SiteTier.Small, "Faction_2", 101, Ctx(0))!;
            SiteService.Enter(site, 10 * D);
            SiteService.Enter(site, 12 * D);
            Assert.Equal(10 * D, site.FirstEntryTick);
            var ctx = Ctx(15 * D);

            ctx.PlayerPresent = true;
            Assert.False(SiteService.ShouldClose(site, ctx));
            ctx.PlayerPresent = false;
            Assert.True(SiteService.ShouldClose(site, ctx));
            Assert.False(SiteService.ShouldClose(site, Ctx(15 * D - 1)));
            Assert.False(SiteService.ShouldClose(site, Ctx(30 * D)) && site.Visited == false);
        }

        [Fact]
        public void Close_RemovesCratesStillAtSite_KeepsTakenOnes()
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", 1, SiteTier.Medium, "Faction_2", 101, Ctx(0))!;
            var taken = site.CargoIds[0];
            CargoService.TakeFromSite(world.Cargo[taken]);

            var d = SiteService.Close(world, site, 20 * D);

            Assert.True(site.Closed);
            Assert.True(world.Cargo.ContainsKey(taken));
            Assert.All(site.CargoIds.Skip(1), id => Assert.False(world.Cargo.ContainsKey(id)));
            Assert.Contains(d.Effects, e => e.Kind == EffectKind.CloseSite && e.EntityId == "S");
        }

        [Fact]
        public void Revisit_InventoryNeverGrows()
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", 1, SiteTier.Medium, "Faction_2", 101, Ctx(0))!;
            SiteService.Enter(site, D);
            var before = SiteService.CratesAtSite(world, site).ToList();
            CargoService.TakeFromSite(world.Cargo[before[0]]);
            site.GuardPointsRemaining = 300m;

            SiteService.Enter(site, 2 * D);
            var after = SiteService.CratesAtSite(world, site).ToList();

            Assert.Equal(before.Count - 1, after.Count);
            Assert.Equal(300m, site.GuardPointsRemaining);
        }

        [Fact]
        public void SiteRecord_SurvivesPersistence()
        {
            var world = Open();
            for (long t = 0; t <= 8 * D; t += CoreTime.CheckIntervalTicks) SiteService.Discover(world, Ctx(t), t);
            world.Sites[0].Vaults[0].Progress = 1234.5m;

            var loaded = StateCodec.Import(StateCodec.Export(world));

            Assert.Equal(world.Sites.Count, loaded.Sites.Count);
            Assert.Equal(1234.5m, loaded.Sites[0].Vaults[0].Progress);
            Assert.Equal(world.Sites[0].Seed, loaded.Sites[0].Seed);
            Assert.Equal(world.NextSiteTick, loaded.NextSiteTick);
        }
    }
}
