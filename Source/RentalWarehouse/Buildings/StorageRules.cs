using HarmonyLib;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Which facility may take which crate, kept in one place so hauling agrees with the ledger (spec 3/5):
    /// a rack never takes a vault-only crate, and a return pod takes only its own contract's returning crates.
    /// </summary>
    public static class StorageRules
    {
        public static bool NeedsVault(Thing t) => CargoAdapter.Comp(t)?.Record?.RequiredStorage == StorageKind.Vault;

        public static bool PodAccepts(Building_ReturnPod pod, Thing t)
        {
            var record = CargoAdapter.Comp(t)?.Record;
            return record != null && record.ContractId == pod.contractId && ReturnAdapter.IsReturning(t);
        }

        /// <summary>
        /// Extra acceptance for <paramref name="owner"/>'s storage settings. A linked storage group cannot tell which member a
        /// hauler will pick, so every pod in the group must accept the crate.
        /// </summary>
        public static bool Accepts(IStoreSettingsParent owner, Thing t)
        {
            switch (owner)
            {
                case Building_ReturnPod pod:
                    return PodAccepts(pod, t);
                case StorageGroup group:
                    foreach (var member in group.members)
                    {
                        if (member is Building_ReturnPod p && !PodAccepts(p, t)) return false;
                    }
                    return true;
                default:
                    return true;
            }
        }
    }

    /// <summary>Crates that must be kept in a vault. The rack's fixed storage settings disallow them, so this needs no Harmony.</summary>
    public class SpecialThingFilterWorker_VaultOnlyCargo : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => t.def == RWDefOf.RW_SealedCargo && StorageRules.NeedsVault(t);

        public override bool CanEverMatch(ThingDef def) => def == RWDefOf.RW_SealedCargo;
    }

    /// <summary>
    /// A pod's acceptance depends on the pod's contract, which a def-level filter cannot express. Every hauling path asks
    /// <see cref="StorageSettings.AllowedToAccept(Thing)"/>, so the per-pod rule is added there; other things return at once.
    /// </summary>
    [HarmonyPatch(typeof(StorageSettings), nameof(StorageSettings.AllowedToAccept), new[] { typeof(Thing) })]
    public static class Patch_StorageSettings_AllowedToAccept
    {
        public static void Postfix(StorageSettings __instance, Thing t, ref bool __result)
        {
            if (__result && t?.def == RWDefOf.RW_SealedCargo) __result = StorageRules.Accepts(__instance.owner, t);
        }
    }
}
