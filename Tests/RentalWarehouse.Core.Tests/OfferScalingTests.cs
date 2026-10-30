using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    /// <summary>Spec 4: offers are generated from the current rank's row of the rank table.</summary>
    public class OfferScalingTests
    {
        private static WorldState WorldAtRank(int rank)
        {
            var world = OfferTests.OpenWorld();
            world.TrustPoints = TrustRanks.Default()[rank].RequiredPoints;
            return world;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void EveryRank_OffersStayInsideThatRanksRangesAndCrateCap(int rank)
        {
            var world = WorldAtRank(rank);
            var row = TrustService.Row(world);

            var offers = OfferTests.RunTo(world, OfferTests.Context(), 90 * D);

            Assert.Equal(rank, TrustService.Rank(world));
            Assert.NotEmpty(offers);
            Assert.Contains(offers, t => t.Kind == ContractKind.Normal);
            Assert.Contains(offers, t => t.Kind == ContractKind.HighRisk);
            foreach (var t in offers)
            {
                bool danger = t.Kind == ContractKind.HighRisk;
                Assert.InRange(t.TotalValue, danger ? row.HighRiskMin : row.NormalMin, danger ? row.HighRiskMax : row.NormalMax);
                Assert.InRange(t.CargoIds.Count, Limits.MinCrates, row.MaxCrates);
            }
        }

        [Fact]
        public void RankSeven_OffersMoreThanFourCrates_AndTheyCanBeAccepted()
        {
            var world = WorldAtRank(7);
            var ctx = OfferTests.Context();
            ContractTerms? big = null;
            long now = 0;

            for (; now <= 180 * D && big == null; now += CoreTime.CheckIntervalTicks)
            {
                big = OfferService.Generate(world, ctx, now).FirstOrDefault(t => t.CargoIds.Count > 4);
            }

            Assert.NotNull(big);
            var accepted = ContractEngine.Accept(big!, Storage(12, 12), world, now);
            Assert.True(accepted.Success, accepted.ReasonKey);
        }

        [Fact]
        public void RankChange_AppliesToNewBatchesOnly()
        {
            var world = OfferTests.OpenWorld();
            var ctx = OfferTests.Context();
            var first = OfferTests.RunTo(world, ctx, D);
            Assert.NotEmpty(first);
            var before = first.Select(t => (t.ContractId, t.TotalValue, t.CargoIds.Count)).ToList();

            world.TrustPoints = 4000;
            var kept = world.Offers.Where(o => first.Any(t => t.ContractId == o.Terms.ContractId))
                .Select(o => (o.Terms.ContractId, o.Terms.TotalValue, o.Terms.CargoIds.Count)).ToList();
            var later = new List<ContractTerms>();
            for (long t = D + CoreTime.CheckIntervalTicks; t <= 60 * D; t += CoreTime.CheckIntervalTicks) later.AddRange(OfferService.Generate(world, ctx, t));

            Assert.Equal(before, kept);
            Assert.NotEmpty(later);
            Assert.All(later, t => Assert.True(t.TotalValue >= (t.Kind == ContractKind.HighRisk ? 20000m : 5000m)));
        }

        [Fact]
        public void EditedRankTable_AppliesToTheNextBatch()
        {
            var world = OfferTests.OpenWorld();
            world.Settings.RankTable[0].NormalMin = 7000m;
            world.Settings.RankTable[0].NormalMax = 7500m;
            world.Settings.RankTable[0].HighRiskMin = 9000m;
            world.Settings.RankTable[0].HighRiskMax = 9500m;

            var offers = OfferTests.RunTo(world, OfferTests.Context(), 30 * D);

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.InRange(t.TotalValue, t.Kind == ContractKind.HighRisk ? 9000m : 7000m, t.Kind == ContractKind.HighRisk ? 9500m : 7500m));
        }

        [Fact]
        public void RowWithMinEqualToMax_NeverExceedsMax()
        {
            var world = OfferTests.OpenWorld();
            var row = world.Settings.RankTable[0];
            row.NormalMin = row.NormalMax = 1000m;
            row.HighRiskMin = row.HighRiskMax = 3000m;
            row.MaxCrates = 1;

            var offers = OfferTests.RunTo(world, OfferTests.Context(), 60 * D);

            Assert.NotEmpty(offers);
            foreach (var t in offers)
            {
                decimal max = t.Kind == ContractKind.HighRisk ? 3000m : 1000m;
                Assert.InRange(t.TotalValue, max * 0.9m, max);
                Assert.Single(t.CargoIds);
            }
        }

        [Fact]
        public void RowWhoseCratesCannotBeBuilt_ProducesNoOfferAndNoException()
        {
            var world = OfferTests.OpenWorld();
            var row = world.Settings.RankTable[0];
            row.NormalMin = row.NormalMax = row.HighRiskMin = row.HighRiskMax = 200000m;
            row.MaxCrates = 1;

            var offers = OfferTests.RunTo(world, OfferTests.Context(), 60 * D);

            Assert.Empty(offers);
            Assert.Empty(world.Offers);
        }
    }
}
