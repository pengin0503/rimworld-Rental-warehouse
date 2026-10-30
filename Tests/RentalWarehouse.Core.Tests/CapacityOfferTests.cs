using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    /// <summary>Offers never ask for more crates than the colony's racks and vaults can hold.</summary>
    public class CapacityOfferTests
    {
        private static WorldState TopRankWorld()
        {
            var world = OfferTests.OpenWorld();
            world.TrustPoints = TrustRanks.Default()[7].RequiredPoints;
            return world;
        }

        [Fact]
        public void NormalOffers_AreCappedAtTheRackCapacity_EvenAtTheTopRank()
        {
            var world = TopRankWorld();
            var ctx = OfferTests.Context();
            ctx.RackCapacity = 2;
            ctx.VaultCapacity = 12;

            var offers = OfferTests.RunTo(world, ctx, 90 * D).Where(t => t.Kind == ContractKind.Normal).ToList();

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.InRange(t.CargoIds.Count, 1, 2));
        }

        [Fact]
        public void HighRiskOffers_AreCappedAtTheVaultCapacity_NotTheRackCapacity()
        {
            var world = TopRankWorld();
            var ctx = OfferTests.Context();
            ctx.RackCapacity = 12;
            ctx.VaultCapacity = 3;

            var offers = OfferTests.RunTo(world, ctx, 90 * D);

            Assert.All(offers.Where(t => t.Kind == ContractKind.HighRisk), t => Assert.InRange(t.CargoIds.Count, 1, 3));
            Assert.Contains(offers.Where(t => t.Kind == ContractKind.Normal), t => t.CargoIds.Count > 3);
        }

        [Fact]
        public void NoFacilities_StillOffersSingleCrateContracts()
        {
            var world = TopRankWorld();
            var ctx = OfferTests.Context();
            ctx.RackCapacity = 0;
            ctx.VaultCapacity = 0;

            var offers = OfferTests.RunTo(world, ctx, 90 * D);

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.Single(t.CargoIds));
        }

        [Fact]
        public void UnknownCapacity_ChangesNothing()
        {
            var a = OfferTests.RunTo(TopRankWorld(), OfferTests.Context(), 90 * D);
            var ctx = OfferTests.Context();
            ctx.RackCapacity = -1;
            ctx.VaultCapacity = -1;
            var b = OfferTests.RunTo(TopRankWorld(), ctx, 90 * D);

            Assert.Equal(a.Select(t => (t.ContractId, t.CargoIds.Count, t.TotalValue)), b.Select(t => (t.ContractId, t.CargoIds.Count, t.TotalValue)));
            Assert.Contains(a, t => t.CargoIds.Count > 4);
        }

        [Fact]
        public void TheSwitchOff_IgnoresTheCapacity()
        {
            var world = TopRankWorld();
            world.Settings.CapacityAwareOffers = false;
            var ctx = OfferTests.Context();
            ctx.RackCapacity = 1;
            ctx.VaultCapacity = 1;

            var offers = OfferTests.RunTo(world, ctx, 90 * D);

            Assert.Contains(offers, t => t.CargoIds.Count > 1);
        }

        [Fact]
        public void CappedOffers_KeepTheirTotalValue_SoEachCrateHoldsMore()
        {
            var world = TopRankWorld();
            var row = TrustService.Row(world);
            var ctx = OfferTests.Context();
            ctx.RackCapacity = 2;
            ctx.VaultCapacity = 2;

            var offers = OfferTests.RunTo(world, ctx, 90 * D);

            Assert.All(offers, t => Assert.InRange(t.TotalValue, t.Kind == ContractKind.HighRisk ? row.HighRiskMin : row.NormalMin,
                t.Kind == ContractKind.HighRisk ? row.HighRiskMax : row.NormalMax));
        }

        [Fact]
        public void CapacityAwareOffers_IsOnByDefault_AndSurvivesCloneAndJson()
        {
            Assert.True(new WarehouseSettings().CapacityAwareOffers);

            var s = new WarehouseSettings { CapacityAwareOffers = false };
            Assert.False(s.Clone().CapacityAwareOffers);

            var world = new WorldState();
            world.Settings.CapacityAwareOffers = false;
            Assert.False(StateCodec.Import(StateCodec.Export(world)).Settings.CapacityAwareOffers);
        }
    }
}
