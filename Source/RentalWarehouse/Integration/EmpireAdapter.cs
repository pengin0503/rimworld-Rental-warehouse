using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// What the Empire of the Royalty DLC adds to the offers (spec 5): whether it exists, the highest Empire title among the
    /// colonists, and the Royalty-only items its contracts are made of. Everything reports "no Empire" without the DLC.
    /// </summary>
    public static class EmpireAdapter
    {
        private const string EmpireDefName = "Empire";
        private const string KnightDefName = "Knight";

        /// <summary>Items that exist only with Royalty (spec 5.3). Missing defs are skipped, so an older or modified install still works.</summary>
        private static readonly string[] CatalogueDefs =
        {
            "MeleeWeapon_MonoSword", "MeleeWeapon_Zeushammer", "MeleeWeapon_PlasmaSword",
            "MeleeWeapon_MonoSwordBladelink", "MeleeWeapon_ZeusHammerBladelink", "MeleeWeapon_PlasmaSwordBladelink",
            "MeleeWeapon_PsyfocusStaff",
            "Apparel_PsyfocusHelmet", "Apparel_EltexSkullcap", "Apparel_PsyfocusShirt", "Apparel_PsyfocusVest", "Apparel_PsyfocusRobe",
            "Apparel_ArmorCataphract", "Apparel_ArmorHelmetCataphract", "Apparel_ArmorMarinePrestige", "Apparel_ArmorLocust",
            "Apparel_ArmorCataphractPhoenix",
            "BroadshieldCore", "Apparel_PackJump", "Apparel_PackBroadshield",
        };

        private static List<Good> catalogue;

        public static bool RoyaltyActive => ModsConfig.RoyaltyActive;

        /// <summary>The Empire faction, or null without Royalty or when this world has none.</summary>
        public static Faction EmpireFaction()
        {
            if (!RoyaltyActive) return null;
            var def = DefDatabase<FactionDef>.GetNamedSilentFail(EmpireDefName);
            return def == null ? null : Find.FactionManager.FirstFactionOfDef(def);
        }

        /// <summary>Highest Empire title seniority among the player's colonists (including caravans), or null if none holds one.</summary>
        public static int? HighestTitleSeniority()
        {
            var empire = EmpireFaction();
            if (empire == null) return null;
            int? best = null;
            foreach (var pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
            {
                var title = pawn.royalty?.GetCurrentTitle(empire);
                if (title != null && (best == null || title.seniority > best.Value)) best = title.seniority;
            }
            return best;
        }

        /// <summary>Seniority of the Knight title, or -1 when the def is missing.</summary>
        public static int KnightSeniority() => DefDatabase<RoyalTitleDef>.GetNamedSilentFail(KnightDefName)?.seniority ?? -1;

        public static decimal TitleFactor() => TrustRanks.EmpireTitleFactor(HighestTitleSeniority(), KnightSeniority());

        public static IList<Good> Catalogue()
        {
            if (catalogue != null) return catalogue;
            catalogue = new List<Good>();
            if (!RoyaltyActive) return catalogue;
            foreach (var defName in CatalogueDefs)
            {
                var good = OfferGenerator.MakeGood(defName, null);
                if (good != null) catalogue.Add(good);
            }
            return catalogue;
        }

        /// <summary>One line saying what is missing for Empire contracts, or null when they are possible or the Empire is not an option.</summary>
        public static string Hint(WarehouseWorldComponent world)
        {
            var empire = EmpireFaction();
            if (empire == null || empire.defeated || empire.HostileTo(Faction.OfPlayer)) return null;
            if (TrustService.Rank(world.State) >= Limits.EmpireMinRank && TitleFactor() > 0m && Catalogue().Count > 0) return null;
            string knight = DefDatabase<RoyalTitleDef>.GetNamedSilentFail(KnightDefName)?.LabelCap.ToString() ?? KnightDefName;
            return "RW_Hint_Empire".Translate(TrustAdapter.RankName(Limits.EmpireMinRank), knight).ToString();
        }
    }
}
