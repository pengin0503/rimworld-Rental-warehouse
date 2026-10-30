using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Feeds the Core offer schedule with the current factions and a priced list of non-perishable goods.</summary>
    public static class OfferGenerator
    {
        /// <summary>Spec 3: vanilla non-perishables only — steel, cloth, silver, gold, components and some equipment.</summary>
        private static readonly (string Def, string Stuff)[] Catalog =
        {
            ("Steel", null),
            ("Cloth", null),
            ("Silver", null),
            ("Gold", null),
            ("ComponentIndustrial", null),
            ("Gun_Revolver", null),
            ("Gun_BoltActionRifle", null),
            ("Gun_AssaultRifle", null),
            ("MeleeWeapon_LongSword", "Steel"),
            ("Apparel_FlakVest", null),
        };

        private static List<Good> goods;

        public static IList<Good> Goods
        {
            get
            {
                if (goods != null) return goods;
                goods = new List<Good>();
                foreach (var (defName, stuffName) in Catalog)
                {
                    var good = MakeGood(defName, stuffName);
                    if (good != null) goods.Add(good);
                }
                return goods;
            }
        }

        /// <summary>
        /// A priced, non-perishable good from a def name (and the stuff it is made of), or null when the def is missing
        /// (for example a DLC that is not active) or has no value.
        /// </summary>
        public static Good MakeGood(string defName, string stuffName)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            var stuff = stuffName == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(stuffName);
            if (def == null || (stuffName != null && stuff == null) || def.IsIngestible || def.HasComp(typeof(CompRottable))) return null;
            if (def.MadeFromStuff && stuff == null) stuff = GenStuff.DefaultStuffFor(def);
            float value = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            float mass = def.GetStatValueAbstract(StatDefOf.Mass, stuff);
            if (value <= 0f) return null;
            int? quality = def.HasComp(typeof(CompQuality)) ? (int)QualityCategory.Normal : (int?)null;
            return new Good(def.defName, def.MadeFromStuff ? stuff?.defName : null, (decimal)value, (decimal)mass, quality);
        }

        public static OfferContext BuildContext()
        {
            var ctx = new OfferContext { Goods = Goods, EmpireTitleFactor = EmpireAdapter.TitleFactor(), EmpireGoods = EmpireAdapter.Catalogue() };
            var empire = EmpireAdapter.EmpireFaction();
            var all = Find.FactionManager.AllFactionsListForReading;
            foreach (var f in all)
            {
                if (f.IsPlayer || f.Hidden || f.temporary) continue;
                var info = new FactionInfo
                {
                    FactionId = Ids.Of(f),
                    Humanlike = f.def.humanlikeFaction,
                    Defeated = f.defeated,
                    HostileToPlayer = f.HostileTo(Faction.OfPlayer),
                    IsEmpire = empire != null && f == empire,
                };
                foreach (var other in all)
                {
                    if (other != f && !other.IsPlayer && f.HostileTo(other)) info.HostileTo.Add(Ids.Of(other));
                }
                ctx.Factions.Add(info);
            }
            return ctx;
        }

        public static void Tick(WarehouseWorldComponent world, long now)
        {
            if (world.State.NextOfferTick == null || now < world.State.NextOfferTick.Value)
            {
                OfferService.ExpireOffers(world.State, now);
                return;
            }
            var created = OfferService.Generate(world.State, BuildContext(), now);
            if (created.Count > 0)
            {
                Messages.Message("RW_Msg_NewOffers".Translate(created.Count), MessageTypeDefOf.NeutralEvent, historical: true);
            }
        }
    }
}
