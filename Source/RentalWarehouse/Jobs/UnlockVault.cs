using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RentalWarehouse
{
    [DefOf]
    public static class RWUnlockDefOf
    {
        public static JobDef RW_UnlockVault;

        static RWUnlockDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(RWUnlockDefOf));
    }

    /// <summary>
    /// Spec 7 "開錠": work on an NPC vault at speed 1 + 0.05 × crafting skill. Progress lives in the site record, so
    /// interruptions, saves and revisits keep it. The first work sounds the alarm.
    /// </summary>
    public class JobDriver_UnlockVault : JobDriver
    {
        private Building_Vault Vault => (Building_Vault)job.targetA.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            this.FailOn(() => VaultSiteHooks.State(Vault) is var v && (v == null || v.Unlocked || v.Destroyed));
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            var work = new Toil
            {
                defaultCompleteMode = ToilCompleteMode.Never,
                tickIntervalAction = delta =>
                {
                    int crafting = pawn.skills?.GetSkill(SkillDefOf.Crafting)?.Level ?? 0;
                    if (VaultSiteHooks.Work(Vault, crafting, delta))
                    {
                        pawn.skills?.Learn(SkillDefOf.Crafting, 0.05f * delta);
                    }
                    var state = VaultSiteHooks.State(Vault);
                    if (state == null || state.Unlocked) ReadyForNextToil();
                },
            };
            work.WithProgressBar(TargetIndex.A, () => (float)VaultService.Fraction(VaultSiteHooks.State(Vault) ?? new VaultState()));
            work.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            work.activeSkill = () => SkillDefOf.Crafting;
            yield return work;
        }
    }
}
