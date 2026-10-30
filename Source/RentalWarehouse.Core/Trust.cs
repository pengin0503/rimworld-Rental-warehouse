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
}
