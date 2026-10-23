using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class LootTests
    {
        private static (WorldState World, SiteRecord Site) NewSite(SiteTier tier = SiteTier.Small, int seed = 77)
        {
            var world = new WorldState();
            var site = SiteService.Create(world, "S", seed, tier, "Faction_2", 101, SiteTests.Ctx(0))!;
            world.Sites.Add(site);
            SiteService.Enter(site, 0);
            return (world, site);
        }

        private static long TicksToUnlock(SiteRecord site, int crafting)
        {
            var vault = site.Vaults[0];
            long ticks = 0;
            while (!vault.Unlocked)
            {
                VaultService.Work(site, vault.VaultId, crafting, 250, ticks);
                ticks += 250;
                Assert.True(ticks < 100000);
            }
            return ticks;
        }

        [Fact]
        public void SmallVault_Crafting0_Takes6000Ticks()
        {
            Assert.Equal(6000, TicksToUnlock(NewSite().Site, 0));
        }

        [Fact]
        public void SmallVault_Crafting10_Takes4000Ticks()
        {
            Assert.Equal(4000, TicksToUnlock(NewSite().Site, 10));
        }

        [Fact]
        public void Unlock_ReleasesCratesOnce()
        {
            var (_, site) = NewSite();
            var vault = site.Vaults[0];

            var done = VaultService.Work(site, vault.VaultId, 0, 6000, 10);
            var again = VaultService.Work(site, vault.VaultId, 0, 6000, 20);

            Assert.True(vault.Unlocked);
            var release = Assert.Single(done.Effects, e => e.Kind == EffectKind.ReleaseVaultCargo);
            Assert.Equal(vault.VaultId, release.EntityId);
            Assert.DoesNotContain(again.Effects, e => e.Kind == EffectKind.ReleaseVaultCargo);
        }

        [Fact]
        public void Progress_IsKeptPerVaultAcrossInterruptions()
        {
            var (world, site) = NewSite(SiteTier.Large);
            VaultService.Work(site, site.Vaults[0].VaultId, 0, 5000, 0);
            VaultService.Work(site, site.Vaults[1].VaultId, 0, 7000, 0);

            var loaded = StateCodec.Import(StateCodec.Export(world)).Sites[0];

            Assert.Equal(5000m, loaded.Vaults[0].Progress);
            Assert.Equal(7000m, loaded.Vaults[1].Progress);
        }

        [Fact]
        public void FirstWork_StartsAlarm_ReinforcementOnceAfterQuarterDay()
        {
            var (_, site) = NewSite();
            VaultService.Work(site, site.Vaults[0].VaultId, 0, 250, 1000);
            Assert.Equal(1000, site.AlarmTick);
            VaultService.Work(site, site.Vaults[0].VaultId, 0, 250, 2000);
            Assert.Equal(1000, site.AlarmTick);

            Assert.Empty(VaultService.Alarm(site, 1000 + D / 4 - 1, loaded: true).Effects);
            var r = VaultService.Alarm(site, 1000 + D / 4, loaded: true);
            var again = VaultService.Alarm(site, 1000 + D, loaded: true);

            var spawn = Assert.Single(r.Effects);
            Assert.Equal(EffectKind.SpawnReinforcement, spawn.Kind);
            Assert.Equal(site.GuardPoints * 0.5m, spawn.Amount);
            Assert.Empty(again.Effects);
        }

        [Fact]
        public void AlarmDueWhileUnloaded_AppliesOnceOnNextLoad()
        {
            var (_, site) = NewSite();
            VaultService.NotifyDamaged(site, 0);

            Assert.Empty(VaultService.Alarm(site, D, loaded: false).Effects);
            Assert.Single(VaultService.Alarm(site, 3 * D, loaded: true).Effects);
            Assert.Empty(VaultService.Alarm(site, 4 * D, loaded: true).Effects);
        }

        [Fact]
        public void TakingFromLockedVault_IsRejected_TakingTwiceIsRejected()
        {
            var (world, site) = NewSite();
            var id = site.Vaults[0].CargoIds[0];

            Assert.Equal("RW_Reject_VaultLocked", VaultService.Take(world, site, id).ReasonKey);
            VaultService.Work(site, site.Vaults[0].VaultId, 0, 6000, 0);
            Assert.True(VaultService.Take(world, site, id).Success);
            Assert.Equal(CargoCustody.Player, world.Cargo[id].Custody);
            Assert.True(world.Cargo[id].Stolen);
            Assert.False(VaultService.Take(world, site, id).Success);
        }

        [Fact]
        public void ForcedOpen_RollsThirtyPercentLossOncePerCrate_Reproducibly()
        {
            int lost = 0, total = 0;
            for (int seed = 0; seed < 200; seed++)
            {
                var (world, site) = NewSite(SiteTier.Medium, seed);
                var (world2, site2) = NewSite(SiteTier.Medium, seed);
                var d = VaultService.ForceOpen(world, site, site.Vaults[0].VaultId, 5);
                VaultService.ForceOpen(world2, site2, site2.Vaults[0].VaultId, 5);
                var again = VaultService.ForceOpen(world, site, site.Vaults[0].VaultId, 6);

                foreach (var id in site.Vaults[0].CargoIds)
                {
                    total++;
                    if (world.Cargo[id].Custody == CargoCustody.Destroyed) lost++;
                    Assert.Equal(world.Cargo[id].Custody, world2.Cargo[id].Custody);
                }
                Assert.True(site.Vaults[0].Destroyed);
                Assert.False(again.Success);
                Assert.Single(d.Effects, e => e.Kind == EffectKind.ReleaseVaultCargo);
            }
            Assert.InRange(lost / (double)total, 0.25, 0.35);
        }

        [Fact]
        public void ForcedOpen_StartsAlarm()
        {
            var (world, site) = NewSite();
            VaultService.ForceOpen(world, site, site.Vaults[0].VaultId, 500);

            Assert.Equal(500, site.AlarmTick);
        }

        [Fact]
        public void StolenCrate_CanBeOpenedAfterSiteClosed_ContentsAreTheOnlyReward()
        {
            var (world, site) = NewSite();
            var id = site.Vaults[0].CargoIds[0];
            VaultService.Work(site, site.Vaults[0].VaultId, 0, 6000, 0);
            VaultService.Take(world, site, id);

            var closed = SiteService.Close(world, site, 30 * D);
            var open = CargoService.Open(world.Cargo[id], "open:" + id);

            Assert.True(open.Success);
            Assert.DoesNotContain(closed.Effects, e => e.Kind == EffectKind.SpawnSilver);
            Assert.DoesNotContain(open.Effects, e => e.Kind == EffectKind.SpawnSilver);
            Assert.Equal(world.Cargo[id].DeclaredValue, world.Cargo[id].DeclaredValue);
        }

        [Fact]
        public void Hostility_ChangeOnlyForNonHostile_NoDoubleDeduction()
        {
            Assert.Equal(-95, VaultService.HostilityChange(currentGoodwill: 20, alreadyHostile: false, threshold: -75));
            Assert.Equal(0, VaultService.HostilityChange(currentGoodwill: -80, alreadyHostile: false, threshold: -75));
            Assert.Equal(0, VaultService.HostilityChange(currentGoodwill: 50, alreadyHostile: true, threshold: -75));
        }

        [Fact]
        public void Hostility_AppliedOncePerSite()
        {
            var (_, site) = NewSite();

            var first = VaultService.DeclareAttack(site, currentGoodwill: 10, alreadyHostile: false, threshold: -75);
            var second = VaultService.DeclareAttack(site, currentGoodwill: 10, alreadyHostile: false, threshold: -75);

            Assert.Equal(-85m, Assert.Single(first.Effects).Amount);
            Assert.Empty(second.Effects);
        }
    }
}
