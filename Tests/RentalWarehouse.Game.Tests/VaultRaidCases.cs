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
    /// <summary>
    /// A08/A10 parts that can be checked on the current map with a stand-in site vault: unlock work, release, taking
    /// and stolen value, forced opening. The full caravan raid with revisit (A09) is a manual step.
    /// </summary>
    public static class VaultRaidCases
    {
        [DebugAction("Rental Warehouse tests", "Vault raid cases (A08/A10)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("VaultRaid", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("UnlockReleasesCratesOnce_TakenCrateCountsAsWealth", UnlockAndTake);
            yield return ("ForcedOpeningRollsLossOnce", ForcedOpening);
        }

        private static (SiteRecord Record, Building_Vault Vault) StandIn(GameTestScope s, int seed)
        {
            var state = s.World.State;
            var ctx = VaultDiscovery.BuildContext(s.World, Ids.Now, needTiles: false);
            var faction = Find.FactionManager.AllFactionsListForReading.First(f => !f.IsPlayer && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer));
            ctx.MinimumGroupPoints.Clear();
            ctx.ThreatPoints = Math.Max(ctx.ThreatPoints, 500m);
            var record = SiteService.Create(state, "GT-Site-" + seed, seed, SiteTier.Small, Ids.Of(faction), 0, ctx);
            Check(record != null, "record");
            state.Sites.Add(record);
            var area = s.FindClearRect(4);
            var vault = (Building_Vault)ThingMaker.MakeThing(RWDefOf.RW_Vault);
            vault.siteId = record.SiteId;
            vault.vaultKey = record.Vaults[0].VaultId;
            vault.SetFaction(faction);
            s.Spawn(vault, area.Min);
            return (record, vault);
        }

        private static void Cleanup(GameTestScope s, SiteRecord record)
        {
            foreach (var id in record.CargoIds)
            {
                var crate = s.World.FindCrate(id);
                if (crate != null) CargoAdapter.RemoveCrate(crate);
                s.World.State.Cargo.Remove(id);
            }
            s.World.State.Sites.Remove(record);
        }

        private static void UnlockAndTake(GameTestScope s)
        {
            var (record, vault) = StandIn(s, 11);
            try
            {
                VaultSiteHooks.Work(vault, 0, (int)record.Vaults[0].RequiredWork);
                Check(record.Vaults[0].Unlocked, "unlocked");
                var released = record.Vaults[0].CargoIds.Select(id => s.World.FindCrate(id)).Where(c => c != null).ToList();
                Equal(record.Vaults[0].CargoIds.Count, released.Count, "released crates");
                // Released again by a duplicate effect: no extra crates.
                EffectDispatcher.Apply(s.World, new EffectRequest { EffectId = "dup", Kind = EffectKind.ReleaseVaultCargo, EntityId = record.Vaults[0].VaultId, Payload = { ["site"] = record.SiteId } });
                Equal(released.Count, s.World.AllCrates.Count(kv => record.CargoIds.Contains(kv.Key)), "no duplicates");

                var take = VaultService.Take(s.World.State, record, record.Vaults[0].CargoIds[0]);
                Check(take.Success, "take: " + take.ReasonKey);
                var crate = released[0];
                Check(Math.Abs(crate.MarketValue - (float)s.World.State.Cargo[record.Vaults[0].CargoIds[0]].DeclaredValue) < 1f, "stolen crate carries its declared value");
            }
            finally
            {
                Cleanup(s, record);
            }
        }

        private static void ForcedOpening(GameTestScope s)
        {
            var (record, vault) = StandIn(s, 12);
            try
            {
                vault.Destroy(DestroyMode.KillFinalize);
                Check(record.Vaults[0].Destroyed && record.Vaults[0].DestructionRolled, "destruction rolled");
                var custody = record.CargoIds.Select(id => s.World.State.Cargo[id].Custody).ToList();
                var again = VaultService.ForceOpen(s.World.State, record, record.Vaults[0].VaultId, Ids.Now);
                Check(!again.Success, "second forced opening is ignored");
                Equal(string.Join(",", custody), string.Join(",", record.CargoIds.Select(id => s.World.State.Cargo[id].Custody)), "custody unchanged");
                Check(record.AlarmTick != null, "alarm started");
            }
            finally
            {
                Cleanup(s, record);
            }
        }
    }
}
