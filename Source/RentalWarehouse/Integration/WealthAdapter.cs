using RentalWarehouse.Core;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Wealth corrections (spec 3). Crates: through <see cref="CompSealedCargo.GetStatOffset"/> on MarketValue (base 0).
    /// Claimable balance: added to the map's item wealth by a Harmony postfix, because it has no Thing on the map.
    /// </summary>
    public static class WealthAdapter
    {
        public static decimal CargoValue(CargoRecord record) => CargoService.WealthValue(record);

        public static float ReceivableFor(Map map)
        {
            var world = WarehouseWorldComponent.Instance;
            if (world == null || map == null) return 0f;
            return (float)world.State.ReceivableBalance(Ids.Of(map));
        }
    }
}
