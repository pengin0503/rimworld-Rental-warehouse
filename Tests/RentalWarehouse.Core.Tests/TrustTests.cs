using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;

namespace RentalWarehouse.Core.Tests
{
    public class TrustTests
    {
        [Fact]
        public void Default_IsTheSpecTable()
        {
            var t = TrustRanks.Default();

            Assert.Equal(TrustRanks.Count, t.Count);
            Assert.Equal(new[] { 0, 30, 100, 250, 500, 1000, 2000, 4000 }, t.Select(r => r.RequiredPoints));
            Assert.Equal(new[] { 4, 4, 5, 6, 7, 8, 9, 10 }, t.Select(r => r.MaxCrates));
            Assert.Equal(new[] { 500m, 750m, 1000m, 1500m, 2000m, 3000m, 4000m, 5000m }, t.Select(r => r.NormalMin));
            Assert.Equal(new[] { 2000m, 3000m, 4000m, 6000m, 8000m, 12000m, 16000m, 20000m }, t.Select(r => r.NormalMax));
            Assert.Equal(new[] { 2000m, 3000m, 4000m, 6000m, 8000m, 12000m, 16000m, 20000m }, t.Select(r => r.HighRiskMin));
            Assert.Equal(new[] { 6000m, 9000m, 12000m, 18000m, 24000m, 36000m, 48000m, 60000m }, t.Select(r => r.HighRiskMax));
        }

        [Fact]
        public void Default_ReturnsAFreshListEveryTime()
        {
            var a = TrustRanks.Default();
            a[0].NormalMax = 1m;

            Assert.Equal(2000m, TrustRanks.Default()[0].NormalMax);
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(29, 0)]
        [InlineData(30, 1)]
        [InlineData(99, 1)]
        [InlineData(100, 2)]
        [InlineData(3999, 6)]
        [InlineData(4000, 7)]
        [InlineData(999999, 7)]
        public void RankOf_UsesTheHighestRowAtOrBelowThePoints(int points, int rank)
        {
            Assert.Equal(rank, TrustRanks.RankOf(TrustRanks.Default(), points));
        }

        [Fact]
        public void PointCap_IsOneAndAHalfTimesTheTopThreshold()
        {
            var t = TrustRanks.Default();
            Assert.Equal(6000, TrustRanks.PointCap(t));

            t[7].RequiredPoints = 100;
            Assert.Equal(150, TrustRanks.PointCap(t));
        }

        [Fact]
        public void Validate_Default_IsNull()
        {
            Assert.Null(TrustRanks.Validate(TrustRanks.Default()));
        }

        [Fact]
        public void Validate_WrongNumberOfRows()
        {
            var t = TrustRanks.Default();
            t.RemoveAt(7);

            Assert.Equal("RW_Rank_WrongCount", TrustRanks.Validate(t));
        }

        [Fact]
        public void Validate_FirstRankMustStartAtZero()
        {
            var t = TrustRanks.Default();
            t[0].RequiredPoints = 1;

            Assert.Equal("RW_Rank_FirstNotZero", TrustRanks.Validate(t));
        }

        [Fact]
        public void Validate_PointsMustStrictlyIncrease()
        {
            var t = TrustRanks.Default();
            t[3].RequiredPoints = 100;

            Assert.Equal("RW_Rank_PointsOrder", TrustRanks.Validate(t));
        }

        [Fact]
        public void Validate_RangesNeedMinAtMostMaxAndAtLeastOneHundred()
        {
            var inverted = TrustRanks.Default();
            inverted[2].NormalMin = inverted[2].NormalMax + 1;
            var invertedHigh = TrustRanks.Default();
            invertedHigh[1].HighRiskMax = invertedHigh[1].HighRiskMin - 1;
            var tiny = TrustRanks.Default();
            tiny[0].NormalMin = 99;

            Assert.Equal("RW_Rank_RangeInvalid", TrustRanks.Validate(inverted));
            Assert.Equal("RW_Rank_RangeInvalid", TrustRanks.Validate(invertedHigh));
            Assert.Equal("RW_Rank_RangeInvalid", TrustRanks.Validate(tiny));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(13)]
        public void Validate_CrateCountMustBeOneToTwelve(int crates)
        {
            var t = TrustRanks.Default();
            t[4].MaxCrates = crates;

            Assert.Equal("RW_Rank_CratesInvalid", TrustRanks.Validate(t));
        }

        [Fact]
        public void ApplyMultiplier_ScalesFromTheBaseRanges()
        {
            var row = new RankRow();

            TrustRanks.ApplyMultiplier(row, 1.5m);
            Assert.Equal((750m, 3000m, 3000m, 9000m), (row.NormalMin, row.NormalMax, row.HighRiskMin, row.HighRiskMax));

            TrustRanks.ApplyMultiplier(row, 3m);
            Assert.Equal((1500m, 6000m, 6000m, 18000m), (row.NormalMin, row.NormalMax, row.HighRiskMin, row.HighRiskMax));
        }

        [Fact]
        public void MultiplierOf_DefaultRows()
        {
            var multipliers = TrustRanks.Default().Select(TrustRanks.MultiplierOf).ToList();

            Assert.Equal(new decimal?[] { 1m, 1.5m, 2m, 3m, 4m, 6m, 8m, 10m }, multipliers);
        }

        [Fact]
        public void MultiplierOf_EditedRow_IsNull()
        {
            var row = TrustRanks.Default()[3];
            row.NormalMax += 1;

            Assert.Null(TrustRanks.MultiplierOf(row));
        }

        [Fact]
        public void ExceedsPerCrateLimit_FlagsOnlyRowsThatCannotBePackedIn33Kilograms()
        {
            Assert.All(TrustRanks.Default(), r => Assert.False(TrustRanks.ExceedsPerCrateLimit(r)));

            var row = TrustRanks.Default()[7];
            row.HighRiskMax = 400000m;
            row.MaxCrates = 10;
            Assert.True(TrustRanks.ExceedsPerCrateLimit(row));
        }

        [Fact]
        public void RankColumns_RoundTripsTheDefaultTable()
        {
            var rows = RankColumns.From(TrustRanks.Default()).ToRows();

            Assert.NotNull(rows);
            Assert.True(TrustRanks.SameTable(TrustRanks.Default(), rows!));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void RankColumns_WithAShortColumn_ReturnsNull(int column)
        {
            var c = RankColumns.From(TrustRanks.Default());
            var lists = new[] { c.Points, c.NormalMin, c.NormalMax, c.HighRiskMin, c.HighRiskMax, c.MaxCrates };
            lists[column].RemoveAt(7);

            Assert.Null(c.ToRows());
        }

        [Fact]
        public void SameTable_DetectsASingleChangedCell_AndDifferentLengths()
        {
            var a = TrustRanks.Default();
            var b = TrustRanks.Default();
            Assert.True(TrustRanks.SameTable(a, b));

            b[5].HighRiskMax += 1;
            Assert.False(TrustRanks.SameTable(a, b));

            b = TrustRanks.Default();
            b.RemoveAt(7);
            Assert.False(TrustRanks.SameTable(a, b));
        }

        [Fact]
        public void Clone_DeepCopiesTheRankTable()
        {
            var settings = new WarehouseSettings();
            var copy = settings.Clone();

            copy.RankTable[0].NormalMax = 1m;

            Assert.Equal(2000m, settings.RankTable[0].NormalMax);
        }

        [Fact]
        public void RankTableAndTrustFields_SurviveJsonRoundTrip()
        {
            var world = new WorldState { TrustPoints = 123, TrustAnnouncedRank = 2 };
            world.Settings.RankTable[2].NormalMax = 4321m;

            var loaded = StateCodec.Import(StateCodec.Export(world));

            Assert.Equal(123, loaded.TrustPoints);
            Assert.Equal(2, loaded.TrustAnnouncedRank);
            Assert.True(TrustRanks.SameTable(world.Settings.RankTable, loaded.Settings.RankTable));
        }

        [Fact]
        public void MissingRankTableInAnOldPayload_UsesTheDefault()
        {
            var loaded = StateCodec.Import(new SavedState { SchemaVersion = 2, Payload = "{\"WorldSeed\":1}" });

            Assert.True(TrustRanks.SameTable(TrustRanks.Default(), loaded.Settings.RankTable));
            Assert.Equal(0, loaded.TrustPoints);
        }
    }
}
