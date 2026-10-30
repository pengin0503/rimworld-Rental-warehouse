using System.Collections.Generic;
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

        // The trust rank table (spec 7) as six columns, one value per rank. Whole silver, so plain ints.
        public List<int> rankPoints;
        public List<int> rankNormalMin;
        public List<int> rankNormalMax;
        public List<int> rankHighMin;
        public List<int> rankHighMax;
        public List<int> rankCrates;

        public RentalWarehouseSettings()
        {
            ResetRanks();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref normalContracts, "normalContracts", true);
            Scribe_Values.Look(ref highRiskContracts, "highRiskContracts", true);
            Scribe_Values.Look(ref vaultSites, "vaultSites", true);
            Scribe_Values.Look(ref nonHostileVaultSites, "nonHostileVaultSites", false);
            Scribe_Values.Look(ref feeMultiplier, "feeMultiplier", 1f);
            Scribe_Values.Look(ref raidScale, "raidScale", 1f);
            Scribe_Collections.Look(ref rankPoints, "rankPoints", LookMode.Value);
            Scribe_Collections.Look(ref rankNormalMin, "rankNormalMin", LookMode.Value);
            Scribe_Collections.Look(ref rankNormalMax, "rankNormalMax", LookMode.Value);
            Scribe_Collections.Look(ref rankHighMin, "rankHighMin", LookMode.Value);
            Scribe_Collections.Look(ref rankHighMax, "rankHighMax", LookMode.Value);
            Scribe_Collections.Look(ref rankCrates, "rankCrates", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) CheckLoadedRanks();
        }

        private RankColumns Columns() => new RankColumns
        {
            Points = rankPoints ?? new List<int>(),
            NormalMin = rankNormalMin ?? new List<int>(),
            NormalMax = rankNormalMax ?? new List<int>(),
            HighRiskMin = rankHighMin ?? new List<int>(),
            HighRiskMax = rankHighMax ?? new List<int>(),
            MaxCrates = rankCrates ?? new List<int>(),
        };

        /// <summary>The rank table, or the default when the stored columns are missing or fail validation.</summary>
        public List<RankRow> RankRows()
        {
            var rows = Columns().ToRows();
            return rows != null && TrustRanks.Validate(rows) == null ? rows : TrustRanks.Default();
        }

        public void SetRankRows(IReadOnlyList<RankRow> rows)
        {
            var c = RankColumns.From(rows);
            rankPoints = c.Points;
            rankNormalMin = c.NormalMin;
            rankNormalMax = c.NormalMax;
            rankHighMin = c.HighRiskMin;
            rankHighMax = c.HighRiskMax;
            rankCrates = c.MaxCrates;
        }

        public void ResetRanks() => SetRankRows(TrustRanks.Default());

        /// <summary>An older settings file has no columns (silently the default); a damaged one is reported once and reset.</summary>
        private void CheckLoadedRanks()
        {
            bool absent = rankPoints == null && rankNormalMin == null && rankNormalMax == null
                && rankHighMin == null && rankHighMax == null && rankCrates == null;
            var rows = Columns().ToRows();
            if (rows != null && TrustRanks.Validate(rows) == null) return;
            if (!absent) Log.Warning("[RentalWarehouse] The saved trust rank table is not valid; the default table is used.");
            ResetRanks();
        }

        public WarehouseSettings ToCore() => new WarehouseSettings
        {
            NormalContractsEnabled = normalContracts,
            HighRiskContractsEnabled = highRiskContracts,
            VaultSitesEnabled = vaultSites,
            NonHostileVaultSitesEnabled = nonHostileVaultSites,
            FeeMultiplier = WarehouseSettings.NormalizeMultiplier((decimal)feeMultiplier),
            RaidScale = WarehouseSettings.NormalizeMultiplier((decimal)raidScale),
            RankTable = RankRows(),
        };

        /// <summary>Copies the settings into the ledger, bumping its version only when something changed.</summary>
        public static void SyncInto(WorldState state)
        {
            var next = RentalWarehouseMod.Settings?.ToCore();
            if (next == null) return;
            var cur = state.Settings;
            if (cur.NormalContractsEnabled == next.NormalContractsEnabled && cur.HighRiskContractsEnabled == next.HighRiskContractsEnabled
                && cur.VaultSitesEnabled == next.VaultSitesEnabled && cur.NonHostileVaultSitesEnabled == next.NonHostileVaultSitesEnabled
                && cur.FeeMultiplier == next.FeeMultiplier && cur.RaidScale == next.RaidScale
                && TrustRanks.SameTable(cur.RankTable, next.RankTable))
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
            if (l.ButtonText("RW_Set_EditRanks".Translate())) Find.WindowStack.Add(new RankTableDialog(s));
            l.Gap();
            l.Label("RW_Set_AppliesToNew".Translate());
            if (l.ButtonText("RW_Set_Reset".Translate()))
            {
                s.normalContracts = s.highRiskContracts = s.vaultSites = true;
                s.nonHostileVaultSites = false;
                s.feeMultiplier = s.raidScale = 1f;
                s.ResetRanks();
            }
            l.End();
        }

        private static float Snap(float v) => (float)WarehouseSettings.NormalizeMultiplier((decimal)v);
    }
}
