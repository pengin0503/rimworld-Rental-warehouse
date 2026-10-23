using RentalWarehouse.Core;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Player settings (spec 8). They apply to new offers and sites only; running contracts keep their terms.</summary>
    public class RentalWarehouseSettings : ModSettings
    {
        public bool normalContracts = true;
        public bool highRiskContracts = true;
        public bool vaultSites = true;
        public bool nonHostileVaultSites;
        public float feeMultiplier = 1f;
        public float raidScale = 1f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref normalContracts, "normalContracts", true);
            Scribe_Values.Look(ref highRiskContracts, "highRiskContracts", true);
            Scribe_Values.Look(ref vaultSites, "vaultSites", true);
            Scribe_Values.Look(ref nonHostileVaultSites, "nonHostileVaultSites", false);
            Scribe_Values.Look(ref feeMultiplier, "feeMultiplier", 1f);
            Scribe_Values.Look(ref raidScale, "raidScale", 1f);
        }

        public WarehouseSettings ToCore() => new WarehouseSettings
        {
            NormalContractsEnabled = normalContracts,
            HighRiskContractsEnabled = highRiskContracts,
            VaultSitesEnabled = vaultSites,
            NonHostileVaultSitesEnabled = nonHostileVaultSites,
            FeeMultiplier = WarehouseSettings.NormalizeMultiplier((decimal)feeMultiplier),
            RaidScale = WarehouseSettings.NormalizeMultiplier((decimal)raidScale),
        };

        /// <summary>Copies the settings into the ledger, bumping its version only when something changed.</summary>
        public static void SyncInto(WorldState state)
        {
            var next = RentalWarehouseMod.Settings?.ToCore();
            if (next == null) return;
            var cur = state.Settings;
            if (cur.NormalContractsEnabled == next.NormalContractsEnabled && cur.HighRiskContractsEnabled == next.HighRiskContractsEnabled
                && cur.VaultSitesEnabled == next.VaultSitesEnabled && cur.NonHostileVaultSitesEnabled == next.NonHostileVaultSitesEnabled
                && cur.FeeMultiplier == next.FeeMultiplier && cur.RaidScale == next.RaidScale)
            {
                return;
            }
            state.Settings = next;
            state.SettingsVersion++;
        }
    }

    public static class SettingsView
    {
        public static void Draw(Rect rect, RentalWarehouseSettings s)
        {
            var l = new Listing_Standard();
            l.Begin(rect);
            l.CheckboxLabeled("RW_Set_Normal".Translate(), ref s.normalContracts);
            l.CheckboxLabeled("RW_Set_HighRisk".Translate(), ref s.highRiskContracts);
            l.CheckboxLabeled("RW_Set_Sites".Translate(), ref s.vaultSites);
            l.CheckboxLabeled("RW_Set_NonHostileSites".Translate(), ref s.nonHostileVaultSites, "RW_Set_NonHostileSitesDesc".Translate());
            l.Gap();
            l.Label("RW_Set_FeeMultiplier".Translate(s.feeMultiplier.ToString("0.00")));
            s.feeMultiplier = Snap(l.Slider(s.feeMultiplier, (float)WarehouseSettings.MultiplierMin, (float)WarehouseSettings.MultiplierMax));
            l.Label("RW_Set_RaidScale".Translate(s.raidScale.ToString("0.00")));
            s.raidScale = Snap(l.Slider(s.raidScale, (float)WarehouseSettings.MultiplierMin, (float)WarehouseSettings.MultiplierMax));
            l.Gap();
            l.Label("RW_Set_AppliesToNew".Translate());
            if (l.ButtonText("RW_Set_Reset".Translate()))
            {
                s.normalContracts = s.highRiskContracts = s.vaultSites = true;
                s.nonHostileVaultSites = false;
                s.feeMultiplier = s.raidScale = 1f;
            }
            l.End();
        }

        private static float Snap(float v) => (float)WarehouseSettings.NormalizeMultiplier((decimal)v);
    }
}
