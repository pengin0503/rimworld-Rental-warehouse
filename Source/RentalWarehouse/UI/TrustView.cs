using RentalWarehouse.Core;
using Verse;

namespace RentalWarehouse
{
    /// <summary>The one-line trust rank summary shown at the top of the offers page (spec 9).</summary>
    public static class TrustView
    {
        public static string Summary(WorldState state)
        {
            string name = TrustAdapter.RankName(TrustService.Rank(state));
            int points = TrustService.Points(state);
            int? next = TrustService.PointsToNextRank(state);
            return next.HasValue
                ? "RW_TrustSummary".Translate(name, points, next.Value).ToString()
                : "RW_TrustSummaryMax".Translate(name, points).ToString();
        }
    }
}
