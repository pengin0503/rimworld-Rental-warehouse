using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace RentalWarehouse
{
    /// <summary>
    /// A vault. The player's vaults hold contract crates; vaults on NPC sites hold that faction's crates and keep their
    /// unlock progress in the site's ledger record (keyed by <see cref="VaultKey"/>).
    /// </summary>
    public class Building_Vault : Building_WarehouseStorage
    {
        public override StorageKind Kind => StorageKind.Vault;

        /// <summary>Stable key inside a site's ledger record. Empty for the player's own vaults.</summary>
        public string siteId;
        public string vaultKey;

        public string VaultKey => vaultKey;
        public bool IsSiteVault => !siteId.NullOrEmpty();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref siteId, "siteId");
            Scribe_Values.Look(ref vaultKey, "vaultKey");
        }

        public override void PostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
        {
            base.PostApplyDamage(dinfo, totalDamageDealt);
            if (IsSiteVault && totalDamageDealt > 0f) VaultSiteHooks.Notify_VaultDamaged(this, dinfo);
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            // Forced opening (spec 7): only real destruction, not the map being discarded.
            if (IsSiteVault && Spawned && (mode == DestroyMode.KillFinalize || mode == DestroyMode.Deconstruct)) VaultSiteHooks.Notify_VaultDestroyed(this);
            base.Destroy(mode);
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn)
        {
            foreach (var o in base.GetFloatMenuOptions(selPawn)) yield return o;
            if (!IsSiteVault) yield break;
            var state = VaultSiteHooks.State(this);
            if (state == null || state.Unlocked || state.Destroyed) yield break;
            if (!selPawn.CanReach(this, PathEndMode.Touch, Danger.Deadly) || !selPawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                yield return new FloatMenuOption("RW_UnlockVault".Translate() + ": " + "RW_CannotReach".Translate(), null);
                yield break;
            }
            yield return new FloatMenuOption("RW_UnlockVault".Translate() + " (" + ((float)VaultService.Fraction(state)).ToStringPercent() + ")", () =>
            {
                selPawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(RWUnlockDefOf.RW_UnlockVault, this), JobTag.Misc);
            });
        }

        public override string GetInspectString()
        {
            var text = base.GetInspectString();
            var state = IsSiteVault ? VaultSiteHooks.State(this) : null;
            if (state == null) return text;
            return text + (text.NullOrEmpty() ? "" : "\n") + "RW_VaultProgress".Translate(((float)VaultService.Fraction(state)).ToStringPercent(), state.CargoIds.Count);
        }
    }
}
