using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RentalWarehouse
{
    [DefOf]
    public static class RWJobDefOf
    {
        public static JobDef RW_StealCargo;
        public static DutyDef RW_StealCargo_Duty;

        static RWJobDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RWJobDefOf));
    }

    /// <summary>
    /// Raid of thieves after one contract's cargo (spec 6). Every pawn gets the cargo-stealing duty. They leave like an
    /// ordinary raid does (after a while, after heavy losses) and also once no contract crate is left to steal (issue #11).
    /// </summary>
    public class LordJob_StealCargo : LordJob
    {
        /// <summary>Looking for stealable crates every tick would be wasteful; twice a second is plenty.</summary>
        private const int CheckEveryTicks = 30;

        public string contractId;

        /// <summary>Tick at which nothing was first seen to be left to steal, or -1 while something is.</summary>
        private int nothingSince = -1;

        public LordJob_StealCargo()
        {
        }

        public LordJob_StealCargo(string contractId)
        {
            this.contractId = contractId;
        }

        public override bool GuiltyOnDowned => true;

        public override StateGraph CreateGraph()
        {
            var graph = new StateGraph();
            var steal = new LordToil_StealCargo();
            var leave = new LordToil_ExitMap(LocomotionUrgency.Jog);
            graph.AddToil(steal);
            graph.AddToil(leave);

            var retreat = new Transition(steal, leave);
            retreat.AddTrigger(new Trigger_TicksPassed(RaidRetreat.GiveUpTicks));
            retreat.AddTrigger(new Trigger_FractionPawnsLost((float)RaidRetreat.CasualtyFraction));
            retreat.AddTrigger(new Trigger_Custom(signal => signal.type == TriggerSignalType.Tick && ShouldRetreat(Find.TickManager.TicksGame)));
            retreat.AddPreAction(new TransitionAction_Message("RW_Msg_RaidersLeave".Translate(), MessageTypeDefOf.NeutralEvent));
            graph.AddTransition(retreat);
            return graph;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref contractId, "contractId");
            Scribe_Values.Look(ref nothingSince, "nothingSince", -1);
        }

        /// <summary>True once no contract crate has been left to steal for a short while, with nobody on the way out carrying one.</summary>
        public bool ShouldRetreat(int now)
        {
            if (lord == null || now % CheckEveryTicks != 0) return false;
            var (since, retreat) = RaidRetreat.Observe(nothingSince < 0 ? (long?)null : nothingSince, ThreatAdapter.AnyStealable(lord.Map), SomeoneCarriesACrate(), now);
            nothingSince = since.HasValue ? (int)since.Value : -1;
            return retreat;
        }

        private bool SomeoneCarriesACrate()
        {
            foreach (var pawn in lord.ownedPawns)
            {
                var carried = pawn.carryTracker?.CarriedThing;
                if (carried != null && CargoAdapter.Comp(carried) != null) return true;
            }
            return false;
        }
    }

    public class LordToil_StealCargo : LordToil
    {
        public override bool ForceHighStoryDanger => true;

        public override bool AllowSatisfyLongNeeds => false;

        public override void UpdateAllDuties()
        {
            foreach (var pawn in lord.ownedPawns) pawn.mindState.duty = new PawnDuty(RWJobDefOf.RW_StealCargo_Duty);
        }
    }

    /// <summary>
    /// Picks a contract crate that is not inside an intact vault: the raid's own contract first, then any contract crate.
    /// </summary>
    public class JobGiver_StealCargo : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (pawn.carryTracker?.CarriedThing != null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return null;
            var world = WarehouseWorldComponent.Instance;
            if (world == null) return null;
            string own = (pawn.GetLord()?.LordJob as LordJob_StealCargo)?.contractId;

            Thing best = null;
            float bestScore = float.MaxValue;
            foreach (var kv in world.AllCrates)
            {
                var crate = kv.Value;
                if (!ThreatAdapter.IsStealable(crate, pawn.Map)) continue;
                var record = CargoAdapter.Comp(crate)?.Record;
                float score = pawn.Position.DistanceToSquared(crate.Position) + (record?.ContractId == own ? 0f : 100000f);
                if (score >= bestScore) continue;
                if (!pawn.CanReserveAndReach(crate, PathEndMode.ClosestTouch, Danger.Deadly)) continue;
                best = crate;
                bestScore = score;
            }
            if (best == null || !RCellFinder.TryFindBestExitSpot(pawn, out var exit)) return null;
            var job = JobMaker.MakeJob(RWJobDefOf.RW_StealCargo, best, exit);
            job.count = 1;
            return job;
        }
    }

    /// <summary>Breaks open a vault that still holds contract crates. Empty vaults are left alone (spec 6).</summary>
    public class JobGiver_BreachVault : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            Building_Vault target = null;
            float best = float.MaxValue;
            foreach (var b in pawn.Map.listerBuildings.allBuildingsColonist)
            {
                if (!(b is Building_Vault vault) || vault.IsSiteVault || !ThreatAdapter.HoldsContractCargo(vault)) continue;
                float d = pawn.Position.DistanceToSquared(vault.Position);
                if (d >= best || !pawn.CanReach(vault, PathEndMode.Touch, Danger.Deadly)) continue;
                target = vault;
                best = d;
            }
            if (target == null) return null;
            var job = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
            job.expiryInterval = 2000;
            job.checkOverrideOnExpire = true;
            return job;
        }
    }

    /// <summary>Walk to the crate, pick it up, carry it to the map edge and leave.</summary>
    public class JobDriver_StealCargo : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed) =>
            pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOn(() => !ThreatAdapter.IsStealable(job.targetA.Thing, pawn.Map) && pawn.carryTracker.CarriedThing != job.targetA.Thing);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A);
            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.OnCell);
            yield return new Toil
            {
                initAction = () =>
                {
                    var map = pawn.Map;
                    if (pawn.Position.OnEdge(map) || map.exitMapGrid.IsExitCell(pawn.Position))
                    {
                        pawn.ExitMap(true, CellRect.WholeMap(map).GetClosestEdge(pawn.Position));
                    }
                },
                defaultCompleteMode = ToilCompleteMode.Instant,
            };
        }
    }
}
