using RentalWarehouse.Core;
using RimWorld;
using Verse;

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
    }
}
