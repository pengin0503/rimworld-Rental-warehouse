using RimWorld;
using Verse;
using Verse.AI;

namespace RentalWarehouse
{
    /// <summary>A raider already holding a crate heads for the nearest exit before doing anything else (spec 6).</summary>
    public class JobGiver_ExitWithCargo : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            var carried = pawn.carryTracker?.CarriedThing;
            if (carried == null || carried.def != RWDefOf.RW_SealedCargo) return null;
            if (!RCellFinder.TryFindBestExitSpot(pawn, out var exit)) return null;
            var job = JobMaker.MakeJob(JobDefOf.Goto, exit);
            job.exitMapOnArrival = true;
            job.locomotionUrgency = LocomotionUrgency.Jog;
            return job;
        }
    }
}
