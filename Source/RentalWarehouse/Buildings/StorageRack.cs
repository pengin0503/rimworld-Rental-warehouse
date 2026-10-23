using System.Collections.Generic;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Common base of the rack and the vault: a storage building that only accepts sealed crates (via the def's fixed
    /// storage settings) and counts capacity in crates.
    /// </summary>
    public abstract class Building_WarehouseStorage : Building_Storage
    {
        public abstract StorageKind Kind { get; }

        public const int CapacityCrates = 4;

        public string FacilityId => "Facility_" + thingIDNumber;

        public IEnumerable<Thing> StoredCrates()
        {
            if (!Spawned) yield break;
            foreach (var cell in AllSlotCellsList())
            {
                var things = cell.GetThingList(Map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (things[i].def == RWDefOf.RW_SealedCargo) yield return things[i];
                }
            }
        }

        public int StoredCount
        {
            get
            {
                int n = 0;
                foreach (var _ in StoredCrates()) n++;
                return n;
            }
        }

        /// <summary>Spec 3: storage counts only under a roof, indoors, in an intact facility.</summary>
        public bool IsValidFacility => FacilityProblem() == null;

        public string FacilityProblem()
        {
            if (!Spawned || Destroyed) return "RW_FeeStop_FacilityGone";
            foreach (var cell in this.OccupiedRect())
            {
                if (!cell.Roofed(Map)) return "RW_FeeStop_NoRoof";
            }
            var room = Position.GetRoom(Map);
            if (room == null || room.PsychologicallyOutdoors) return "RW_FeeStop_Outdoors";
            return null;
        }

        public bool Satisfies(StorageKind required) => required == StorageKind.Rack || Kind == StorageKind.Vault;

        public override AcceptanceReport DeconstructibleBy(Faction faction)
        {
            if (Faction == Faction.OfPlayer && StoredCount > 0) return "RW_Reject_FacilityNotEmpty".Translate();
            return base.DeconstructibleBy(faction);
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("RW_FacilityCapacity".Translate(StoredCount, CapacityCrates));
            var problem = FacilityProblem();
            if (problem != null)
            {
                sb.AppendLine();
                sb.Append(problem.Translate());
            }
            return sb.ToString();
        }
    }

    public class Building_StorageRack : Building_WarehouseStorage
    {
        public override StorageKind Kind => StorageKind.Rack;
    }
}
