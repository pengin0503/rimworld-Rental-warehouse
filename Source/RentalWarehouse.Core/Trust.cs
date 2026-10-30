using System;
using System.Collections.Generic;

namespace RentalWarehouse.Core
{
    /// <summary>One row of the trust rank table (spec 3.3): the points it needs and the offers it allows.</summary>
    public sealed class RankRow
    {
        public int RequiredPoints;
        public decimal NormalMin;
        public decimal NormalMax;
        public decimal HighRiskMin;
        public decimal HighRiskMax;
        public int MaxCrates;

        public RankRow Clone() => (RankRow)MemberwiseClone();
    }

    /// <summary>The rank table as six parallel columns, the shape the mod settings file stores.</summary>
    public sealed class RankColumns
    {
        public List<int> Points = new List<int>();
        public List<int> NormalMin = new List<int>();
        public List<int> NormalMax = new List<int>();
        public List<int> HighRiskMin = new List<int>();
        public List<int> HighRiskMax = new List<int>();
        public List<int> MaxCrates = new List<int>();

        public static RankColumns From(IReadOnlyList<RankRow> rows)
        {
            var c = new RankColumns();
            foreach (var r in rows)
            {
                c.Points.Add(r.RequiredPoints);
                c.NormalMin.Add((int)r.NormalMin);
                c.NormalMax.Add((int)r.NormalMax);
                c.HighRiskMin.Add((int)r.HighRiskMin);
                c.HighRiskMax.Add((int)r.HighRiskMax);
                c.MaxCrates.Add(r.MaxCrates);
            }
            return c;
        }

        /// <summary>The rows, or null when a column does not have exactly one value per rank.</summary>
        public List<RankRow>? ToRows()
        {
            foreach (var column in new[] { Points, NormalMin, NormalMax, HighRiskMin, HighRiskMax, MaxCrates })
            {
                if (column == null || column.Count != TrustRanks.Count) return null;
            }
            var rows = new List<RankRow>();
            for (int i = 0; i < TrustRanks.Count; i++)
            {
                rows.Add(new RankRow
                {
                    RequiredPoints = Points[i],
                    NormalMin = NormalMin[i],
                    NormalMax = NormalMax[i],
                    HighRiskMin = HighRiskMin[i],
                    HighRiskMax = HighRiskMax[i],
                    MaxCrates = MaxCrates[i],
                });
            }
            return rows;
        }
    }

    /// <summary>The trust rank table (spec 3.3) and the arithmetic around it. Pure functions of a table.</summary>
    public static class TrustRanks
    {
        public const int Count = 8;
        public const decimal BaseNormalMin = 500m;
        public const decimal BaseNormalMax = 2000m;
        public const decimal BaseHighRiskMin = 2000m;
        public const decimal BaseHighRiskMax = 6000m;
        /// <summary>Smallest total value a rank may offer.</summary>
        public const decimal MinTotal = 100m;
        /// <summary>A crate holds 33 kg, so a row asking more than this per crate may not be buildable (spec 7).</summary>
        public const decimal PerCrateWarnValue = 30000m;
        private const decimal CapFactor = 1.5m;

        private static readonly int[] DefaultPoints = { 0, 30, 100, 250, 500, 1000, 2000, 4000 };
        private static readonly decimal[] DefaultMultipliers = { 1m, 1.5m, 2m, 3m, 4m, 6m, 8m, 10m };
        private static readonly int[] DefaultCrates = { 4, 4, 5, 6, 7, 8, 9, 10 };

        public static List<RankRow> Default()
        {
            var rows = new List<RankRow>();
            for (int i = 0; i < Count; i++)
            {
                var row = new RankRow { RequiredPoints = DefaultPoints[i], MaxCrates = DefaultCrates[i] };
                ApplyMultiplier(row, DefaultMultipliers[i]);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>The highest rank whose required points the points reach.</summary>
        public static int RankOf(IReadOnlyList<RankRow> table, int points)
        {
            int rank = 0;
            for (int i = 0; i < table.Count; i++)
            {
                if (points >= table[i].RequiredPoints) rank = i;
            }
            return rank;
        }

        /// <summary>The most points that can be held: one and a half times the top rank's requirement.</summary>
        public static int PointCap(IReadOnlyList<RankRow> table) =>
            table.Count == 0 ? 0 : (int)Math.Floor(table[table.Count - 1].RequiredPoints * CapFactor);

        /// <summary>A reason key when the table cannot be used, null when it is valid.</summary>
        public static string? Validate(IReadOnlyList<RankRow> table)
        {
            if (table.Count != Count) return "RW_Rank_WrongCount";
            if (table[0].RequiredPoints != 0) return "RW_Rank_FirstNotZero";
            for (int i = 1; i < table.Count; i++)
            {
                if (table[i].RequiredPoints <= table[i - 1].RequiredPoints) return "RW_Rank_PointsOrder";
            }
            foreach (var r in table)
            {
                if (r.NormalMin < MinTotal || r.HighRiskMin < MinTotal || r.NormalMin > r.NormalMax || r.HighRiskMin > r.HighRiskMax)
                {
                    return "RW_Rank_RangeInvalid";
                }
            }
            foreach (var r in table)
            {
                if (r.MaxCrates < 1 || r.MaxCrates > Limits.HardMaxCrates) return "RW_Rank_CratesInvalid";
            }
            return null;
        }

        public static bool ExceedsPerCrateLimit(RankRow row) =>
            Math.Max(row.NormalMax, row.HighRiskMax) / Math.Max(1, row.MaxCrates) > PerCrateWarnValue;

        /// <summary>Sets the four totals to the base ranges times <paramref name="multiplier"/>, rounded to whole silver.</summary>
        public static void ApplyMultiplier(RankRow row, decimal multiplier)
        {
            row.NormalMin = Scale(BaseNormalMin, multiplier);
            row.NormalMax = Scale(BaseNormalMax, multiplier);
            row.HighRiskMin = Scale(BaseHighRiskMin, multiplier);
            row.HighRiskMax = Scale(BaseHighRiskMax, multiplier);
        }

        /// <summary>The multiplier that produces this row's four totals, or null when they were edited individually.</summary>
        public static decimal? MultiplierOf(RankRow row)
        {
            decimal m = row.NormalMin / BaseNormalMin;
            if (m <= 0m) return null;
            var probe = row.Clone();
            ApplyMultiplier(probe, m);
            bool same = probe.NormalMin == row.NormalMin && probe.NormalMax == row.NormalMax
                && probe.HighRiskMin == row.HighRiskMin && probe.HighRiskMax == row.HighRiskMax;
            return same ? m : (decimal?)null;
        }

        public static bool SameTable(IReadOnlyList<RankRow> a, IReadOnlyList<RankRow> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x.RequiredPoints != y.RequiredPoints || x.NormalMin != y.NormalMin || x.NormalMax != y.NormalMax
                    || x.HighRiskMin != y.HighRiskMin || x.HighRiskMax != y.HighRiskMax || x.MaxCrates != y.MaxCrates)
                {
                    return false;
                }
            }
            return true;
        }

        private static decimal Scale(decimal value, decimal multiplier) =>
            Math.Round(value * multiplier, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Trust points of one ledger: how settlements change them and which rank they make (spec 3).</summary>
    public static class TrustService
    {
        private const decimal PointValue = 100m;
        private const decimal LossWeight = 2m;
        private const decimal EmbezzleWeight = 4m;
        private const decimal AbandonWeight = 0.5m;

        /// <summary>The table in force: the ledger's own, or the default when the ledger holds an unusable one.</summary>
        private static List<RankRow> Table(WorldState world) =>
            TrustRanks.Validate(world.Settings.RankTable) == null ? world.Settings.RankTable : TrustRanks.Default();

        /// <summary>The saved points held within 0 and the current cap.</summary>
        public static int Points(WorldState world) =>
            Math.Max(0, Math.Min(world.TrustPoints, TrustRanks.PointCap(Table(world))));

        public static int Rank(WorldState world) => TrustRanks.RankOf(Table(world), Points(world));

        public static RankRow Row(WorldState world) => Table(world)[Rank(world)];

        /// <summary>Points still needed for the next rank, or null at the top rank.</summary>
        public static int? PointsToNextRank(WorldState world)
        {
            var table = Table(world);
            int rank = Rank(world);
            return rank + 1 < table.Count ? table[rank + 1].RequiredPoints - Points(world) : (int?)null;
        }

        /// <summary>
        /// Points a settled contract earns or loses (spec 3.2): returned value on a full term earns, lost and embezzled
        /// value costs, an early return or a failed storage costs half the contract's value. System cancellations are neutral.
        /// </summary>
        public static int DeltaFor(ContractState c)
        {
            if (c.SettlementCause == SettlementCause.SystemCancelled) return 0;
            decimal returned = 0m, lost = 0m, embezzled = 0m;
            foreach (var cs in c.CargoStates)
            {
                if (cs.Returned) returned += cs.DeclaredValue;
                else if (cs.Loss == LossCause.Lost) lost += cs.DeclaredValue;
                else if (cs.Loss == LossCause.Embezzled) embezzled += cs.DeclaredValue;
            }
            decimal gain = c.ReturnReason == ReturnReason.Matured ? returned / PointValue : 0m;
            decimal loss = lost / PointValue * LossWeight + embezzled / PointValue * EmbezzleWeight;
            bool abandoned = c.ReturnReason == ReturnReason.EarlyByPlayer || c.ReturnReason == ReturnReason.StorageFailed;
            decimal abandon = abandoned ? c.Terms.TotalValue / PointValue * AbandonWeight : 0m;
            return (int)Math.Round(gain - loss - abandon, 0, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Adds <paramref name="delta"/> to the points. A loss never takes the points below the requirement of the rank
        /// one step below (two for the Empire) the rank held before it (spec 3.4).
        /// </summary>
        public static (int Before, int After) Apply(WorldState world, int delta, bool empire)
        {
            var table = Table(world);
            int before = Points(world);
            int after = before + delta;
            if (delta < 0)
            {
                int floorRank = Math.Max(0, TrustRanks.RankOf(table, before) - (empire ? 2 : 1));
                after = Math.Max(after, table[floorRank].RequiredPoints);
            }
            after = Math.Max(0, Math.Min(after, TrustRanks.PointCap(table)));
            world.TrustPoints = after;
            return (before, after);
        }

        /// <summary>The rank change not yet announced, or null. Marks it announced.</summary>
        public static (int From, int To)? TakeRankChange(WorldState world)
        {
            int rank = Rank(world);
            if (rank == world.TrustAnnouncedRank) return null;
            var change = (world.TrustAnnouncedRank, rank);
            world.TrustAnnouncedRank = rank;
            return change;
        }
    }
}
