using System.Collections.Generic;
using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// An independent vault site on the world map (spec 7, approach A). Never touches existing settlements. Its map
    /// exists only while the player is there; leaving writes the state back to the ledger (see
    /// docs/technical/1.7-api-audit.md §3) and revisits rebuild from it without restocking.
    /// </summary>
    public class VaultSite : MapParent
    {
        public string siteId;

        public static readonly IntVec3 MapSize = new IntVec3(120, 1, 120);

        public SiteRecord Record => WarehouseWorldComponent.Instance == null ? null : SiteService.Site(WarehouseWorldComponent.Instance.State, siteId);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref siteId, "siteId");
        }

        public override string Label => "RW_SiteLabel".Translate(Faction?.Name ?? "?", ("RW_Tier_" + (Record?.Tier ?? SiteTier.Small)).Translate());

        public override MapGeneratorDef MapGeneratorDef => MapGeneratorDefOf.Encounter;

        public override IEnumerable<GenStepWithParams> ExtraGenStepDefs
        {
            get
            {
                foreach (var g in base.ExtraGenStepDefs) yield return g;
                yield return new GenStepWithParams(VaultMapGenerator.GenStepDef, default(GenStepParams));
            }
        }

        protected override bool UseGenericEnterMapFloatMenuOption => false;

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            var record = Record;
            alsoRemoveWorldObject = record == null || record.Closed;
            return HasMap && !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }

        public override void Notify_MyMapAboutToBeRemoved()
        {
            base.Notify_MyMapAboutToBeRemoved();
            VaultMapGenerator.WriteBack(this, Map);
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (var o in base.GetFloatMenuOptions(caravan)) yield return o;
            var record = Record;
            if (record == null || record.Closed) yield break;
            foreach (var o in CaravanArrivalActionUtility.GetFloatMenuOptions(
                () => true,
                () => new CaravanArrivalAction_EnterVaultSite(this),
                "RW_EnterVaultSite".Translate(Label),
                caravan, Tile, this))
            {
                yield return o;
            }
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var record = Record;
            var world = WarehouseWorldComponent.Instance;
            if (record != null && world != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(VaultInfoView.Describe(world, record));
            }
            return sb.ToString();
        }
    }

    public class CaravanArrivalAction_EnterVaultSite : CaravanArrivalAction
    {
        private VaultSite site;

        public CaravanArrivalAction_EnterVaultSite()
        {
        }

        public CaravanArrivalAction_EnterVaultSite(VaultSite site)
        {
            this.site = site;
        }

        public override string Label => "RW_EnterVaultSite".Translate(site?.Label ?? "");

        public override string ReportString => "RW_EnteringVaultSite".Translate(site?.Label ?? "");

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            var baseReport = base.StillValid(caravan, destinationTile);
            if (!baseReport) return baseReport;
            return site != null && site.Tile == destinationTile && site.Record != null && !site.Record.Closed;
        }

        public override void Arrived(Caravan caravan)
        {
            var map = site.Map ?? GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, VaultSite.MapSize, site.def);
            CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop, draftColonists: true);
            var record = site.Record;
            if (record != null)
            {
                bool first = record.FirstEntryTick == null;
                SiteService.Enter(record, Ids.Now);
                if (first) Find.LetterStack.ReceiveLetter("RW_Letter_SiteEnteredTitle".Translate(), "RW_Letter_SiteEntered".Translate(site.Label,
                    ((int)VaultSiteParameters.WithdrawalAfterFirstEntry).ToStringTicksToPeriod()), LetterDefOf.NeutralEvent, caravan.PawnsListForReading.FirstOrDefault());
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref site, "site");
        }
    }
}
