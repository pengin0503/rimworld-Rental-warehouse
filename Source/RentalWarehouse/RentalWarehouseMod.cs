using System;
using System.Runtime.CompilerServices;
using Verse;

namespace RentalWarehouse
{
    public class RentalWarehouseMod : Mod
    {
        public RentalWarehouseMod(ModContentPack content) : base(content)
        {
            HarmonyGate.TryPatch();
        }
    }

    /// <summary>
    /// Applies the few Harmony patches (see docs/technical/1.7-api-audit.md 4.1). Without Harmony those features degrade
    /// with one error in the log; contracts, defence and vault raids keep working.
    /// </summary>
    public static class HarmonyGate
    {
        public static bool Active { get; private set; }

        public static void TryPatch()
        {
            try
            {
                Patch();
                Active = true;
            }
            catch (Exception e)
            {
                Log.Error("[RentalWarehouse] Harmony patches unavailable; contract crates may be loaded into caravans and the claimable balance is not counted as wealth. " + e);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Patch() => new HarmonyLib.Harmony("pengin0503.RentalWarehouse").PatchAll(typeof(HarmonyGate).Assembly);
    }
}
