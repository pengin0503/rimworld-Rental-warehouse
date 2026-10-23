using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Feeds the Core reconciler (spec 8): which maps, factions and defs exist, and why a contract map disappeared.
    /// </summary>
    public static class LifecycleAdapter
    {
        public const int IntervalTicks = 1000;

        private static HashSet<string> defNames;

        /// <summary>ThingDef names never change while the game runs, so they are collected once.</summary>
        public static HashSet<string> DefNames => defNames ??= new HashSet<string>(DefDatabase<ThingDef>.AllDefsListForReading.Select(d => d.defName));

        public static void Tick(WarehouseWorldComponent world, long now)
        {
            if (now % IntervalTicks != 0) return;
            var d = LifecycleService.Reconcile(world.State, Snapshot(world), now);
            world.Enqueue(d.Effects, applyNow: false);
            PaymentService.SyncDebts(world.State, now);
            foreach (var c in world.State.Contracts)
            {
                if (c.IsTerminal) StorageQuest.RemoveLeftoverPod(c);
            }
        }

        public static WorldSnapshot Snapshot(WarehouseWorldComponent world)
        {
            var snap = new WorldSnapshot { DefNames = DefNames };
            foreach (var map in Find.Maps) snap.MapIds.Add(Ids.Of(map));
            foreach (var kv in world.State.RemovedMaps) snap.RemovedMapCauses[kv.Key] = kv.Value;
            foreach (var f in Find.FactionManager.AllFactionsListForReading)
            {
                if (f.IsPlayer || f.defeated) continue;
                string id = Ids.Of(f);
                snap.FactionIds.Add(id);
                if (f.HostileTo(Faction.OfPlayer)) snap.HostileFactionIds.Add(id);
            }
            return snap;
        }

        public static bool HasOpenContracts(Map map)
        {
            var world = WarehouseWorldComponent.Instance;
            string id = Ids.Of(map);
            return world != null && world.State.Contracts.Any(c => !c.IsTerminal && c.MapId == id);
        }

        internal static readonly HashSet<string> AbandonedByPlayer = new HashSet<string>();
        internal static readonly HashSet<MapParent> AbandonConfirmed = new HashSet<MapParent>();
    }

    /// <summary>Records why a map went away, so the reconciler can tell abandonment from other causes.</summary>
    public class MapComponent_WarehouseWatch : MapComponent
    {
        public MapComponent_WarehouseWatch(Map map) : base(map)
        {
        }

        public override void MapRemoved()
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null) return;
            world.ForgetCratesOn(map);
            string id = Ids.Of(map);
            if (!world.State.Contracts.Exists(c => c.MapId == id)) return;
            world.State.RemovedMaps[id] = LifecycleAdapter.AbandonedByPlayer.Remove(id) ? MapRemovalCause.AbandonedByPlayer : MapRemovalCause.Unknown;
        }
    }

    /// <summary>Spec 8: warn before the player abandons a map that holds contract cargo.</summary>
    [HarmonyPatch(typeof(SettlementAbandonUtility), nameof(SettlementAbandonUtility.TryAbandonViaInterface))]
    public static class Patch_SettlementAbandonUtility_TryAbandonViaInterface
    {
        public static bool Prefix(MapParent settlement)
        {
            if (settlement?.Map == null || !LifecycleAdapter.HasOpenContracts(settlement.Map)) return true;
            if (LifecycleAdapter.AbandonConfirmed.Remove(settlement)) return true;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RW_AbandonWarning".Translate(), () =>
            {
                LifecycleAdapter.AbandonConfirmed.Add(settlement);
                SettlementAbandonUtility.TryAbandonViaInterface(settlement);
            }, destructive: true));
            return false;
        }
    }

    [HarmonyPatch(typeof(MapParent), nameof(MapParent.Abandon))]
    public static class Patch_MapParent_Abandon
    {
        public static void Prefix(MapParent __instance)
        {
            if (__instance?.Map != null) LifecycleAdapter.AbandonedByPlayer.Add(Ids.Of(__instance.Map));
        }
    }
}
