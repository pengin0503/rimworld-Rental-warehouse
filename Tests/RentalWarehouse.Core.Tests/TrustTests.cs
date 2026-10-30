using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

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
        public void RankColumns_WithABadValue_StillBuildsRows_ButValidationRejectsThem()
        {
            var c = RankColumns.From(TrustRanks.Default());
            c.Points[3] = -1;

            var rows = c.ToRows();

            Assert.NotNull(rows);
            Assert.Equal("RW_Rank_PointsOrder", TrustRanks.Validate(rows!));
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

        // ---- settlement deltas (spec 3.2) ----

        private static ContractState Matured(params decimal[] values)
        {
            var terms = Terms(ContractKind.Normal, "K1", values);
            var world = World(terms);
            var c = Activate(world, terms);
            RunUntil(c, c.EndTick!.Value);
            return c;
        }

        private static List<EffectRequest> ReturnAllCollecting(ContractState c, long now)
        {
            var effects = new List<EffectRequest>();
            foreach (var cs in c.CargoStates.Where(x => x.Loss == LossCause.None && !x.Returned).ToList())
            {
                effects.AddRange(ContractEngine.MarkReturned(c, cs.CargoId, now).Effects);
            }
            return effects;
        }

        [Fact]
        public void Delta_FullyReturnedAtMaturity()
        {
            var c = Matured(1000m);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(10, c.Settlement!.TrustDelta);
        }

        [Fact]
        public void Delta_OneLostOneReturned()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            ContractEngine.MarkLost(c, terms.CargoIds[1], LossCause.Lost, c.ActiveTick!.Value + 4 * D);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(10 - 20, c.Settlement!.TrustDelta);
        }

        [Fact]
        public void Delta_OneEmbezzledOneReturned()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            ContractEngine.MarkLost(c, terms.CargoIds[1], LossCause.Embezzled, c.ActiveTick!.Value + 3 * D);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(10 - 40, c.Settlement!.TrustDelta);
        }

        [Fact]
        public void Delta_EarlyReturnByPlayer()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long t = c.ActiveTick!.Value + D;
            RunUntil(c, t);
            Assert.True(ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, t).Success);
            ReturnAll(c, t + 10);

            Assert.Equal(-5, c.Settlement!.TrustDelta);
        }

        [Fact]
        public void Delta_StorageFailed()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            ContractEngine.MarkDelivered(c, terms.CargoIds[0], D);
            ContractEngine.Advance(c, Storage(4), 2 * D);
            ReturnAll(c, 2 * D + 100);

            Assert.Equal(ReturnReason.StorageFailed, c.ReturnReason);
            Assert.Equal(-5, c.Settlement!.TrustDelta);
        }

        [Fact]
        public void Delta_DepositorHostileAllReturnedInWindow_IsZero_AndEmitsNoEffect()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long t = c.ActiveTick!.Value + D;
            RunUntil(c, t);
            Assert.True(ContractEngine.RequestEarlyReturn(c, ReturnReason.DepositorHostile, t).Success);
            var effects = ReturnAllCollecting(c, t + 10);

            Assert.Equal(0, c.Settlement!.TrustDelta);
            Assert.DoesNotContain(effects, e => e.Kind == EffectKind.ApplyTrust);
        }

        [Fact]
        public void Delta_SystemCancelledIsZeroEvenWithALoss()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long t = c.ActiveTick!.Value + D;
            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, t);

            var d = ContractEngine.SystemCancel(c, "RW_Diag_Test", t + D);

            Assert.True(d.Success);
            Assert.Equal(SettlementCause.SystemCancelled, c.SettlementCause);
            Assert.Equal(0, c.Settlement!.TrustDelta);
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.ApplyTrust);
        }

        [Fact]
        public void Delta_RoundsHalfAwayFromZero()
        {
            var gain = Matured(250m);
            ReturnAll(gain, gain.EndTick!.Value + 10);

            var terms = Terms(ContractKind.Normal, "K2", new[] { 500m });
            var world = World(terms);
            var early = Activate(world, terms);
            long t = early.ActiveTick!.Value + D;
            RunUntil(early, t);
            ContractEngine.RequestEarlyReturn(early, ReturnReason.EarlyByPlayer, t);
            ReturnAll(early, t + 10);

            Assert.Equal(3, gain.Settlement!.TrustDelta);
            Assert.Equal(-3, early.Settlement!.TrustDelta);
        }

        [Theory]
        [InlineData(false, "0")]
        [InlineData(true, "1")]
        public void Settlement_EmitsOneApplyTrustEffectWithAStableId(bool empire, string flag)
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            terms.DepositorIsEmpire = empire;
            var world = World(terms);
            var c = Activate(world, terms);
            RunUntil(c, c.EndTick!.Value);

            var effects = ReturnAllCollecting(c, c.EndTick!.Value + 10);

            var trust = Assert.Single(effects, e => e.Kind == EffectKind.ApplyTrust);
            Assert.Equal("K1:settle:trust", trust.EffectId);
            Assert.Equal("K1", trust.EntityId);
            Assert.Equal(10m, trust.Amount);
            Assert.Equal(flag, trust.Payload["empire"]);
        }

        // ---- applying points (spec 3.4, 3.6) ----

        [Fact]
        public void Apply_DemotionIsLimitedToOneRankStep()
        {
            var w = new WorldState { TrustPoints = 1000 };
            Assert.Equal((1000, 500), TrustService.Apply(w, -720, empire: false));
            Assert.Equal(500, w.TrustPoints);

            var e = new WorldState { TrustPoints = 1000 };
            Assert.Equal((1000, 280), TrustService.Apply(e, -720, empire: true));

            var small = new WorldState { TrustPoints = 1000 };
            Assert.Equal((1000, 900), TrustService.Apply(small, -100, empire: false));
        }

        [Fact]
        public void Apply_FloorsAtZero_AndAtTheCap()
        {
            var a = new WorldState { TrustPoints = 20 };
            Assert.Equal((20, 0), TrustService.Apply(a, -50, empire: false));

            var b = new WorldState { TrustPoints = 30 };
            Assert.Equal((30, 0), TrustService.Apply(b, -100, empire: true));

            var c = new WorldState { TrustPoints = 5990 };
            Assert.Equal((5990, 6000), TrustService.Apply(c, 100, empire: false));
        }

        [Fact]
        public void Points_ClampsTheSavedValueToTheCurrentCap()
        {
            Assert.Equal(6000, TrustService.Points(new WorldState { TrustPoints = 9999 }));
            Assert.Equal(0, TrustService.Points(new WorldState { TrustPoints = -5 }));
        }

        [Fact]
        public void RankAndRow_FollowThePoints()
        {
            var w = new WorldState { TrustPoints = 250 };

            Assert.Equal(3, TrustService.Rank(w));
            Assert.Equal(6, TrustService.Row(w).MaxCrates);
        }

        [Theory]
        [InlineData(0, 30)]
        [InlineData(42, 58)]
        [InlineData(99, 1)]
        [InlineData(3999, 1)]
        public void PointsToNextRank_CountsDownToTheNextThreshold(int points, int expected)
        {
            Assert.Equal(expected, TrustService.PointsToNextRank(new WorldState { TrustPoints = points }));
        }

        [Fact]
        public void PointsToNextRank_IsNullAtTheTopRank()
        {
            Assert.Null(TrustService.PointsToNextRank(new WorldState { TrustPoints = 4000 }));
            Assert.Null(TrustService.PointsToNextRank(new WorldState { TrustPoints = 6000 }));
        }

        [Fact]
        public void AnInvalidLedgerTable_FallsBackToTheDefault()
        {
            var w = new WorldState { TrustPoints = 250 };
            w.Settings.RankTable.RemoveAt(7);

            Assert.Equal(3, TrustService.Rank(w));
            Assert.Equal(6, TrustService.Row(w).MaxCrates);
            Assert.Equal((250, 30), TrustService.Apply(w, -1000, empire: true));
        }

        [Fact]
        public void TakeRankChange_ReportsOnceThenNull()
        {
            var w = new WorldState { TrustPoints = 30 };

            Assert.Equal((0, 1), TrustService.TakeRankChange(w));
            Assert.Equal(1, w.TrustAnnouncedRank);
            Assert.Null(TrustService.TakeRankChange(w));
        }

        [Fact]
        public void TakeRankChange_NoticesAThresholdEditedInTheSettings_AndADemotion()
        {
            var w = new WorldState { TrustPoints = 20 };
            w.Settings.RankTable[1].RequiredPoints = 10;
            Assert.Equal((0, 1), TrustService.TakeRankChange(w));

            var d = new WorldState { TrustPoints = 0, TrustAnnouncedRank = 3 };
            Assert.Equal((3, 0), TrustService.TakeRankChange(d));
        }
    }
}
