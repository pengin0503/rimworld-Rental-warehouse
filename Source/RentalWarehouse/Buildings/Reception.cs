using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Front desk. Needed to accept new contracts on its map; existing contracts continue without it (spec 3/8).</summary>
    public class Building_Reception : Building
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos()) yield return g;
            if (Faction != Faction.OfPlayer) yield break;
            var world = WarehouseWorldComponent.Instance;
            if (world == null) yield break;
            yield return new Command_Toggle
            {
                defaultLabel = "RW_BusinessOpen".Translate(),
                defaultDesc = "RW_BusinessOpenDesc".Translate(),
                icon = TexCommand.DesirePower,
                isActive = () => world.State.BusinessOpen,
                toggleAction = () => world.SetBusinessOpen(!world.State.BusinessOpen),
            };
            yield return new Command_Action
            {
                defaultLabel = "RW_OpenManagement".Translate(),
                defaultDesc = "RW_OpenManagementDesc".Translate(),
                icon = TexCommand.Install,
                action = WarehouseTab.Open,
            };
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            var world = WarehouseWorldComponent.Instance;
            if (world != null)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(world.State.BusinessOpen ? "RW_BusinessIsOpen".Translate() : "RW_BusinessIsClosed".Translate());
            }
            return sb.ToString();
        }

        public static bool AnyOn(Map map) =>
            map != null && map.listerBuildings.ColonistsHaveBuilding(RWDefOf.RW_Reception);
    }
}
