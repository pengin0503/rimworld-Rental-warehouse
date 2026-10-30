using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Announces trust rank changes (spec 3.6). Points change through ApplyTrust effects and when the rank table is
    /// edited; either way the change is reported once, as a plain message with no ceremony.
    /// </summary>
    public static class TrustAdapter
    {
        public static string RankName(int rank) => ("RW_TrustRank_" + rank).Translate().ToString();

        public static void Tick(WarehouseWorldComponent world)
        {
            var change = TrustService.TakeRankChange(world.State);
            if (change == null) return;
            bool up = change.Value.To > change.Value.From;
            Messages.Message((up ? "RW_Msg_RankUp" : "RW_Msg_RankDown").Translate(RankName(change.Value.To)),
                up ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent, historical: true);
        }
    }
}
