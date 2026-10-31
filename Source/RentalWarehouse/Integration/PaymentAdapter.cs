using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Silver in and out of the colony for claims and debt payments.</summary>
    public static class PaymentAdapter
    {
        public static string Destination(IntVec3 cell) =>
            "Cell_" + cell.x.ToString(CultureInfo.InvariantCulture) + "_" + cell.z.ToString(CultureInfo.InvariantCulture);

        public static bool TryParseDestination(string id, Map map, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            if (id == null || !id.StartsWith("Cell_")) return false;
            var parts = id.Substring(5).Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
            {
                return false;
            }
            cell = new IntVec3(x, 0, z);
            return cell.InBounds(map);
        }

        /// <summary>The current valid delivery point for silver on this map; invalid when there is none.</summary>
        public static string DefaultDestination(Map map) => map == null ? "" : Destination(DeliveryAdapter.DeliverySpot(map));

        /// <summary>Silver the colony can spend on this map: unforbidden, in storage or the home area.</summary>
        public static List<Thing> SpendableSilver(Map map)
        {
            var list = new List<Thing>();
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
            {
                if (!t.Spawned || t.IsForbidden(Faction.OfPlayer)) continue;
                if (t.IsInAnyStorage() || map.areaManager.Home[t.Position]) list.Add(t);
            }
            return list;
        }

        public static int SpendableSilverCount(Map map)
        {
            int n = 0;
            foreach (var t in SpendableSilver(map)) n += t.stackCount;
            return n;
        }

        /// <summary>Drops claimed silver by pod. A destination that became invalid falls back to the map's drop spot.</summary>
        public static bool SpawnSilver(WarehouseWorldComponent world, EffectRequest effect)
        {
            var map = Ids.Map(effect.EntityId) ?? Find.AnyPlayerHomeMap;
            if (map == null) return false;
            effect.Payload.TryGetValue("destination", out var dest);
            var spot = TryParseDestination(dest, map, out var cell) && DropCellFinder.IsGoodDropSpot(cell, map, allowFogged: false, canRoofPunch: false)
                ? cell
                : DeliveryAdapter.DeliverySpot(map);
            var batch = world.Batch(effect.EffectId);
            batch.Prepare(() => MakeSilver((int)decimal.Floor(effect.Amount)));
            return batch.Transfer(silver => DropPodUtility.DropThingsNear(spot, map, new List<Thing> { silver },
                canRoofPunch: false, forbid: false, allowFogged: false));
        }

        private static List<Thing> MakeSilver(int amount)
        {
            var things = new List<Thing>();
            while (amount > 0)
            {
                var silver = ThingMaker.MakeThing(ThingDefOf.Silver);
                silver.stackCount = Mathf.Min(amount, ThingDefOf.Silver.stackLimit);
                amount -= silver.stackCount;
                things.Add(silver);
            }
            return things;
        }

        /// <summary>Reserve real silver in a saved owner before committing the debt. Retries see the reserved remainder.</summary>
        public static bool ConsumeSilver(WarehouseWorldComponent world, EffectRequest effect)
        {
            var batch = world.Batch(effect.EffectId);
            if (!batch.Committed)
            {
                var map = Ids.Map(effect.EntityId);
                effect.Payload.TryGetValue("debt", out var debtId);
                if (map == null || debtId != null && !world.State.Debts.Exists(d => d.DebtId == debtId)) batch.Refunding = true;
                if (batch.Refunding)
                {
                    if (batch.Contents.Count > 0)
                    {
                        var home = Find.AnyPlayerHomeMap;
                        if (home == null) return false;
                        var spot = DeliveryAdapter.DeliverySpot(home);
                        if (!batch.Transfer(t => DropPodUtility.DropThingsNear(spot, home, new List<Thing> { t },
                            canRoofPunch: false, forbid: false, allowFogged: false))) return false;
                    }
                    PaymentService.ReopenPayment(world.State, effect);
                    return true;
                }
                int reserved = 0;
                foreach (var t in batch.Contents) reserved += t.stackCount;
                int needed = (int)decimal.Ceiling(effect.Amount) - reserved;
                var available = SpendableSilver(map);
                int count = 0;
                foreach (var t in available) count += t.stackCount;
                if (count < needed) return false; // keep the debt and existing reservation until full payment is possible
                foreach (var t in available)
                {
                    if (needed <= 0) break;
                    needed -= batch.Contents.TryAddOrTransfer(t, Mathf.Min(needed, t.stackCount), canMergeWithExistingStacks: false);
                }
                if (needed > 0) return false;
                batch.Committed = true; // before destroying the reservation; a failed destroy must never charge again
            }
            PaymentService.CompletePayment(world.State, effect);
            foreach (var t in new List<Thing>(batch.Contents)) t.Destroy(DestroyMode.Vanish);
            return batch.Contents.Count == 0;
        }
    }

    /// <summary>
    /// Spec 3: the claimable balance counts as colony wealth even though no silver exists on the map yet. Added to the
    /// item total once per recount, so WealthItems and WealthTotal both include it exactly once whichever way the game
    /// combines them.
    /// </summary>
    [HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
    public static class Patch_WealthWatcher_ForceRecount
    {
        private static AccessTools.FieldRef<WealthWatcher, Map> mapField;
        private static AccessTools.FieldRef<WealthWatcher, float> itemsField;
        private static bool disabled;

        /// <summary>Never lets a renamed private field break the game's own wealth recount; the correction is dropped instead.</summary>
        public static void Postfix(WealthWatcher __instance, bool allowDuringInit)
        {
            // ForceRecount returns early without recounting outside play unless allowDuringInit; adding then would stack.
            if (disabled || !allowDuringInit && Current.ProgramState != ProgramState.Playing) return;
            try
            {
                mapField ??= AccessTools.FieldRefAccess<WealthWatcher, Map>("map");
                itemsField ??= AccessTools.FieldRefAccess<WealthWatcher, float>("wealthItems");
                itemsField(__instance) += WealthAdapter.ReceivableFor(mapField(__instance));
            }
            catch (System.Exception e)
            {
                disabled = true;
                Log.Error("[RentalWarehouse] Claimable balance can no longer be added to colony wealth: " + e.Message);
            }
        }
    }
}
