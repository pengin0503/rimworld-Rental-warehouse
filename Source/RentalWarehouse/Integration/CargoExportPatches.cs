using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Spec 3: crates held for another faction may not leave the map by caravan or transport pod.</summary>
    public static class CargoExportRules
    {
        public static bool IsLocked(Thing thing)
        {
            var record = CargoAdapter.Comp(thing)?.Record;
            return record != null && record.ContractId != null && record.Custody == CargoCustody.Depositor;
        }
    }

    [HarmonyPatch(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.AllReachableColonyItems))]
    public static class Patch_CaravanFormingUtility_AllReachableColonyItems
    {
        public static void Postfix(List<Thing> __result) => __result?.RemoveAll(CargoExportRules.IsLocked);
    }

    [HarmonyPatch(typeof(TransporterUtility), nameof(TransporterUtility.AllSendableItems))]
    public static class Patch_TransporterUtility_AllSendableItems
    {
        public static void Postfix(ref IEnumerable<Thing> __result)
        {
            if (__result != null) __result = __result.Where(t => !CargoExportRules.IsLocked(t)).ToList();
        }
    }
}
