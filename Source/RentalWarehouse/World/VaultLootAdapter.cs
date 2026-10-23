using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Loot from NPC vaults (spec 7): crates appear when a vault opens, become the player's (stolen, original owner kept)
    /// once a colonist or caravan holds them, and are rewarded only by their contents — no silver bonus.
    /// </summary>
    public static class VaultLootAdapter
    {
        /// <summary>Where a destroyed vault stood, recorded before it despawns so released crates land there.</summary>
        public static readonly Dictionary<string, (Map Map, IntVec3 Cell)> PendingReleasePosition = new Dictionary<string, (Map, IntVec3)>();

        public static bool ReleaseVaultCargo(WarehouseWorldComponent world, EffectRequest effect)
        {
            effect.Payload.TryGetValue("site", out var siteId);
            var record = siteId == null ? null : SiteService.Site(world.State, siteId);
            var site = record == null ? null : VaultDiscovery.FindSite(record.SiteId);
            if (record == null || record.Closed) return true;
            var vaultState = record.Vaults.FirstOrDefault(v => v.VaultId == effect.EntityId);
            if (vaultState == null) return true;

            Map map;
            IntVec3 cell;
            if (PendingReleasePosition.TryGetValue(vaultState.VaultId, out var pending) && pending.Map != null && Find.Maps.Contains(pending.Map))
            {
                (map, cell) = pending;
                PendingReleasePosition.Remove(vaultState.VaultId);
            }
            else
            {
                if (site == null || !site.HasMap) return true; // rebuilt from the record on the next visit
                var vault = site.Map.listerThings.ThingsOfDef(RWDefOf.RW_Vault).OfType<Building_Vault>().FirstOrDefault(v => v.vaultKey == vaultState.VaultId);
                map = site.Map;
                cell = vault?.Position ?? site.Map.Center;
            }
            foreach (var id in vaultState.CargoIds)
            {
                if (!world.State.Cargo.TryGetValue(id, out var c) || c.Custody != CargoCustody.Depositor) continue;
                if (world.FindCrate(id) != null) continue; // reconciled: already released
                var crate = CargoAdapter.MakeCrate(c);
                crate.SetForbidden(false, warnOnFail: false);
                GenPlace.TryPlaceThing(crate, cell, map, ThingPlaceMode.Near);
            }
            Messages.Message("RW_Msg_VaultOpened".Translate(), new TargetInfo(cell, map), MessageTypeDefOf.PositiveEvent, historical: false);
            return true;
        }

        /// <summary>A site crate held by a player pawn or caravan, or lying on a player home map, now belongs to the player.</summary>
        public static void Tick(WarehouseWorldComponent world, long now)
        {
            foreach (var kv in world.AllCrates.ToList())
            {
                if (!world.State.Cargo.TryGetValue(kv.Key, out var c) || c.SiteId == null || c.Custody != CargoCustody.Depositor) continue;
                var crate = kv.Value;
                if (crate.Destroyed || !HeldByPlayer(crate)) continue;
                var record = SiteService.Site(world.State, c.SiteId);
                if (record == null) continue;
                VaultService.Take(world.State, record, kv.Key);
            }
        }

        private static bool HeldByPlayer(Thing crate)
        {
            var holder = ThreatAdapter.Carrier(crate);
            if (holder != null) return holder.Faction == Faction.OfPlayer;
            if (crate.Spawned && crate.Map.IsPlayerHome) return true;
            return crate.ParentHolder is Caravan caravan && caravan.IsPlayerControlled;
        }
    }
}
