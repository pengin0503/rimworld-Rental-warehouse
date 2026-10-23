using System.Linq;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RentalWarehouse
{
    /// <summary>Connects NPC vault buildings to their site record: unlock work, damage, forced opening and the alarm.</summary>
    public static class VaultSiteHooks
    {
        public static SiteRecord Site(Building_Vault vault)
        {
            var world = WarehouseWorldComponent.Instance;
            return world == null || vault?.siteId == null ? null : SiteService.Site(world.State, vault.siteId);
        }

        public static VaultState State(Building_Vault vault) => Site(vault)?.Vaults.FirstOrDefault(v => v.VaultId == vault.vaultKey);

        /// <summary>Returns true when progress was recorded.</summary>
        public static bool Work(Building_Vault vault, int crafting, int ticks)
        {
            var world = WarehouseWorldComponent.Instance;
            var site = Site(vault);
            if (world == null || site == null) return false;
            var d = VaultService.Work(site, vault.vaultKey, crafting, ticks, Ids.Now);
            if (!d.Success) return false;
            if (d.Effects.Count > 0) VaultLootAdapter.PendingReleasePosition[vault.vaultKey] = (vault.Map, vault.Position);
            world.Enqueue(d.Effects);
            return true;
        }

        public static void Notify_VaultDamaged(Building_Vault vault, DamageInfo dinfo)
        {
            var site = Site(vault);
            if (site != null) VaultService.NotifyDamaged(site, Ids.Now);
        }

        public static void Notify_VaultDestroyed(Building_Vault vault)
        {
            var world = WarehouseWorldComponent.Instance;
            var site = Site(vault);
            if (world == null || site == null) return;
            var d = VaultService.ForceOpen(world.State, site, vault.vaultKey, Ids.Now);
            if (!d.Success) return;
            // The vault is still spawned here, so released crates land where it stood.
            VaultLootAdapter.PendingReleasePosition[vault.vaultKey] = (vault.Map, vault.Position);
            world.Enqueue(d.Effects);
            int lost = site.Vaults.First(v => v.VaultId == vault.vaultKey).CargoIds.Count(id => !world.State.Cargo.TryGetValue(id, out var c) || c.Custody == CargoCustody.Destroyed);
            if (lost > 0) Messages.Message("RW_Msg_ForcedOpenLoss".Translate(lost), MessageTypeDefOf.NegativeEvent, historical: true);
        }

        /// <summary>Alarm check for every site; reinforcement only on a loaded map, once.</summary>
        public static void Tick(WarehouseWorldComponent world, long now)
        {
            foreach (var record in world.State.Sites)
            {
                if (record.Closed || record.AlarmTick == null || record.ReinforcementApplied) continue;
                var site = VaultDiscovery.FindSite(record.SiteId);
                world.Enqueue(VaultService.Alarm(record, now, site != null && site.HasMap).Effects, applyNow: false);
            }
        }

        public static void Notify_MapLoaded(WarehouseWorldComponent world, VaultSite site, SiteRecord record)
        {
            world.Enqueue(VaultService.Alarm(record, Ids.Now, loaded: true).Effects);
        }

        /// <summary>SpawnReinforcement effect. If no group can be formed the reinforcement is skipped, not retried (spec 7).</summary>
        public static bool SpawnReinforcement(WarehouseWorldComponent world, EffectRequest effect)
        {
            var record = SiteService.Site(world.State, effect.EntityId);
            var site = record == null ? null : VaultDiscovery.FindSite(record.SiteId);
            var faction = record == null ? null : Ids.Faction(record.FactionId);
            if (site == null || !site.HasMap || faction == null || faction.defeated) return true;
            var map = site.Map;
            float points = (float)effect.Amount;
            if (points < faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)) return true;
            if (!RCellFinder.TryFindRandomPawnEntryCell(out var entry, map, CellFinder.EdgeRoadChance_Hostile)) return true;
            var pawns = PawnGroupMakerUtility.GeneratePawns(new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                tile = map.Tile,
                faction = faction,
                points = points,
                generateFightersOnly = true,
            }).ToList();
            if (pawns.Count == 0) return true;
            foreach (var p in pawns) GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(entry, map, 8), map);
            LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: false, canTimeoutOrFlee: true, sappers: false, useAvoidGridSmart: false, canSteal: false), map, pawns);
            Find.LetterStack.ReceiveLetter("RW_Letter_ReinforcementTitle".Translate(), "RW_Letter_Reinforcement".Translate(faction.Name, pawns.Count), LetterDefOf.ThreatBig, new LookTargets(pawns));
            return true;
        }
    }
}
