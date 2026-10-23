using System;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Spec 7: before raiding a vault of a non-hostile faction, show the diplomatic result and ask. The goodwill change
    /// is applied once, on arrival, only as far as needed to make the faction hostile.
    /// </summary>
    public static class AttackVaultDialog
    {
        public static Action<Action> ConfirmProxy(VaultSite site) => proceed =>
        {
            var faction = site.Faction;
            if (faction == null || faction.HostileTo(Faction.OfPlayer))
            {
                proceed();
                return;
            }
            int change = VaultService.HostilityChange(faction.GoodwillWith(Faction.OfPlayer), false, HostileThreshold);
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RW_AttackFriendlyConfirm".Translate(faction.Name, change, HostileThreshold), proceed, destructive: true));
        };

        /// <summary>
        /// The 1.7 reference assemblies expose no public hostility-threshold constant (docs/technical/1.7-api-audit.md),
        /// so the spec's design value −75 is used; crossing it makes the faction hostile in vanilla relations.
        /// </summary>
        public static int HostileThreshold => VaultSiteParameters.HostileGoodwillTarget;

        public static void OnArrived(VaultSite site)
        {
            var world = WarehouseWorldComponent.Instance;
            var record = site.Record;
            var faction = site.Faction;
            if (world == null || record == null || faction == null) return;
            var d = VaultService.DeclareAttack(record, faction.GoodwillWith(Faction.OfPlayer), faction.HostileTo(Faction.OfPlayer), HostileThreshold);
            world.Enqueue(d.Effects);
        }
    }
}
