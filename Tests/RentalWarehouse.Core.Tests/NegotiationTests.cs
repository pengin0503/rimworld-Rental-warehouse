using System;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    /// <summary>Spec 6: the player may ask for a higher prepayment once per offer; the outcome is fixed by the offer's seed.</summary>
    public class NegotiationTests
    {
        private static int SeedWhere(Func<decimal, bool> accept)
        {
            for (int seed = 1; seed < 200000; seed++)
            {
                if (accept(NegotiationService.Roll(seed))) return seed;
            }
            throw new InvalidOperationException("no seed with the wanted roll");
        }

        /// <summary>One offer with a 10% opening prepayment (or <paramref name="prepay"/>) whose roll is fixed by <paramref name="seed"/>.</summary>
        private static (WorldState World, ContractTerms Terms) Offer(int seed, decimal prepay = 0.10m, int points = 0)
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: prepay);
            terms.Seed = seed;
            var world = World(terms);
            world.TrustPoints = points;
            return (world, terms);
        }

        [Fact]
        public void SuccessChance_MatchesTheSpecTable()
        {
            Assert.Equal(0.775m, NegotiationService.SuccessChance(10, 15, 5, 0));
            Assert.Equal(0.76m, NegotiationService.SuccessChance(10, 20, 10, 2));
            Assert.Equal(0.36m, NegotiationService.SuccessChance(10, 30, 10, 2));
            Assert.Equal(0.30m, NegotiationService.SuccessChance(5, 20, 2, 0));
            Assert.Equal(0.175m, NegotiationService.SuccessChance(0, 30, 15, 5));
        }

        [Fact]
        public void SuccessChance_ClampsToFiveAndNinetyFivePercent()
        {
            Assert.Equal(0.05m, NegotiationService.SuccessChance(0, 30, 0, 0));
            Assert.Equal(0.95m, NegotiationService.SuccessChance(29, 30, 20, 7));
        }

        [Fact]
        public void SuccessChance_ClampsSocialSkillToZeroThroughTwenty()
        {
            Assert.Equal(NegotiationService.SuccessChance(10, 11, 20, 0), NegotiationService.SuccessChance(10, 11, 40, 0));
            Assert.Equal(NegotiationService.SuccessChance(10, 11, 0, 0), NegotiationService.SuccessChance(10, 11, -5, 0));
        }

        [Fact]
        public void SuccessChance_RisesWithSkillAndRank_AndFallsWithTheRequest()
        {
            decimal baseChance = NegotiationService.SuccessChance(10, 20, 8, 1);

            Assert.True(NegotiationService.SuccessChance(10, 20, 9, 1) > baseChance);
            Assert.True(NegotiationService.SuccessChance(10, 20, 8, 2) > baseChance);
            Assert.True(NegotiationService.SuccessChance(10, 21, 8, 1) < baseChance);
        }

        [Fact]
        public void Attempt_RollBelowChance_Succeeds_AndChangesOnlyThePrepayment()
        {
            var (world, terms) = Offer(SeedWhere(r => r < 0.05m));
            decimal reward = terms.NominalTotalReward;
            decimal completion = terms.CompletionRate;

            var d = NegotiationService.Attempt(world, "K1", 15, socialLevel: 0, now: D);

            Assert.True(d.Success);
            Assert.Equal(NegotiationState.Succeeded, d.NewState.Negotiation);
            Assert.Equal(0.15m, terms.PrepaymentRate);
            Assert.Equal(0.10m, terms.OpeningPrepaymentRate);
            Assert.Equal(completion, terms.CompletionRate);
            Assert.Equal(reward, terms.NominalTotalReward);
            Assert.Equal(9m, Accounting.Prepayment(terms));
        }

        [Fact]
        public void Attempt_Succeeding_NeverCostsCompletionBonus_EvenForTheBiggestRaise()
        {
            var (world, terms) = Offer(SeedWhere(r => r < 0.05m), prepay: 0m, points: 4000);
            var before = (terms.CompletionRate, terms.NominalTotalReward, terms.DailyBase, terms.DailyValueRate, terms.FeeMultiplier);

            var d = NegotiationService.Attempt(world, "K1", 44, socialLevel: 20, now: D);

            Assert.Equal(NegotiationState.Succeeded, d.NewState.Negotiation);
            Assert.Equal(0.44m, terms.PrepaymentRate);
            Assert.Equal(before, (terms.CompletionRate, terms.NominalTotalReward, terms.DailyBase, terms.DailyValueRate, terms.FeeMultiplier));
        }

        [Theory]
        [InlineData(0, 30)]
        [InlineData(1, 32)]
        [InlineData(3, 36)]
        [InlineData(7, 44)]
        public void MaxPercentFor_RisesTwoPointsPerRank(int rank, int expected)
        {
            Assert.Equal(expected, NegotiationService.MaxPercentFor(rank));
        }

        [Theory]
        [InlineData(250, 36, true)]
        [InlineData(250, 37, false)]
        [InlineData(4000, 44, true)]
        [InlineData(4000, 45, false)]
        [InlineData(0, 31, false)]
        public void Attempt_TheHighestRateAllowedFollowsTheRank(int points, int requested, bool allowed)
        {
            var (world, _) = Offer(1, prepay: 0.10m, points: points);

            var d = NegotiationService.Attempt(world, "K1", requested, 10, D);

            Assert.Equal(allowed, d.Success);
            if (!allowed) Assert.Equal("RW_Reject_InvalidRate", d.ReasonKey);
        }

        [Fact]
        public void Attempt_AnOpeningAtTheRankLimit_LeavesNoRoom_ButAHigherRankDoes()
        {
            var (low, _) = Offer(1, prepay: 0.30m, points: 0);
            var (high, _) = Offer(1, prepay: 0.30m, points: 250);

            Assert.Equal("RW_Reject_InvalidRate", NegotiationService.Attempt(low, "K1", 31, 10, D).ReasonKey);
            Assert.True(NegotiationService.Attempt(high, "K1", 31, 10, D).Success);
        }

        [Fact]
        public void NegotiatedAboveThirty_FlowsIntoTheAcceptedContract()
        {
            var (world, terms) = Offer(SeedWhere(r => r < 0.05m), prepay: 0.30m, points: 4000);
            Assert.Equal(NegotiationState.Succeeded, NegotiationService.Attempt(world, "K1", 40, 0, D).NewState.Negotiation);

            var c = Activate(world, terms);

            Assert.Equal(0.40m, c.Terms.PrepaymentRate);
            Assert.Equal(24m, c.Receivable);
        }

        [Fact]
        public void Attempt_RollAtOrAboveChance_Fails_AndKeepsTheOpeningRate()
        {
            var (world, terms) = Offer(SeedWhere(r => r >= 0.95m), points: 4000);

            var d = NegotiationService.Attempt(world, "K1", 11, socialLevel: 20, now: D);

            Assert.True(d.Success);
            Assert.Equal(NegotiationState.Failed, d.NewState.Negotiation);
            Assert.Equal(0.10m, terms.PrepaymentRate);
        }

        [Fact]
        public void Attempt_Twice_RejectsTheSecond_WhateverTheFirstOutcome()
        {
            foreach (bool succeed in new[] { true, false })
            {
                var (world, terms) = Offer(SeedWhere(r => succeed ? r < 0.05m : r >= 0.95m), points: succeed ? 0 : 4000);
                NegotiationService.Attempt(world, "K1", 11, succeed ? 0 : 20, D);
                decimal after = terms.PrepaymentRate;

                var second = NegotiationService.Attempt(world, "K1", 25, 20, D);

                Assert.False(second.Success);
                Assert.Equal("RW_Reject_AlreadyNegotiated", second.ReasonKey);
                Assert.Equal(after, terms.PrepaymentRate);
            }
        }

        [Theory]
        [InlineData(10)]
        [InlineData(9)]
        [InlineData(31)]
        public void Attempt_RejectsRatesThatAreNotAboveTheOpeningOrAboveTheRankLimit(int requested)
        {
            var (world, terms) = Offer(1);

            var d = NegotiationService.Attempt(world, "K1", requested, 10, D);

            Assert.False(d.Success);
            Assert.Equal("RW_Reject_InvalidRate", d.ReasonKey);
            Assert.Equal(NegotiationState.None, world.Offers.Single().Negotiation);
            Assert.Equal(0.10m, terms.PrepaymentRate);
        }

        [Theory]
        [InlineData(30)]
        [InlineData(31)]
        public void Attempt_WhenTheOpeningIsAlreadyAtTheRankLimit_NothingCanBeAsked(int requested)
        {
            var (world, _) = Offer(1, prepay: 0.30m);

            var d = NegotiationService.Attempt(world, "K1", requested, 10, D);

            Assert.Equal("RW_Reject_InvalidRate", d.ReasonKey);
        }

        [Fact]
        public void Attempt_RejectsExpiredAndUnknownAndAlreadyAcceptedOffers()
        {
            var (world, terms) = Offer(1);

            Assert.Equal("RW_Reject_OfferExpired", NegotiationService.Attempt(world, "K1", 15, 10, terms.OfferExpiresTick).ReasonKey);
            Assert.Equal("RW_Reject_NotOffered", NegotiationService.Attempt(world, "Nope", 15, 10, D).ReasonKey);

            Assert.True(ContractEngine.Accept(terms, Storage(4), world, 0).Success);
            Assert.Equal("RW_Reject_NotOffered", NegotiationService.Attempt(world, "K1", 15, 10, D).ReasonKey);
        }

        [Fact]
        public void Attempt_GivesTheSameResultAfterSaveAndLoad()
        {
            var (world, _) = Offer(SeedWhere(r => r >= 0.4m && r < 0.6m));
            var copy = StateCodec.Import(StateCodec.Export(world));

            var a = NegotiationService.Attempt(world, "K1", 14, 6, D);
            var b = NegotiationService.Attempt(copy, "K1", 14, 6, D);

            Assert.Equal(a.NewState.Negotiation, b.NewState.Negotiation);
            Assert.Equal(world.Offers.Single().Terms.PrepaymentRate, copy.Offers.Single().Terms.PrepaymentRate);
        }

        [Fact]
        public void NegotiatedRate_FlowsIntoTheAcceptedContract()
        {
            var (world, terms) = Offer(SeedWhere(r => r < 0.05m));
            Assert.Equal(NegotiationState.Succeeded, NegotiationService.Attempt(world, "K1", 15, 0, D).NewState.Negotiation);

            var c = Activate(world, terms);

            Assert.Equal(9m, c.Receivable);
            Assert.Equal(0.15m, c.Terms.PrepaymentRate);
        }

        [Fact]
        public void Validation_RejectsAPrepaymentAboveTheHighestNegotiableRate()
        {
            var (world, terms) = Offer(1);
            terms.PrepaymentRate = 0.45m;

            Assert.Equal("RW_Reject_InvalidTerms", ContractEngine.Accept(terms, Storage(4), world, 0).ReasonKey);
        }

        [Fact]
        public void Migration_V2_OfferedTermsBecomeNegotiable_AndMigrationIsIdempotent()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.20m);
            terms.OpeningPrepaymentRate = 0m;
            var world = World(terms);
            world.SchemaVersion = 2;

            var upgraded = Migration.Upgrade(StateCodec.Export(world));
            var loaded = StateCodec.Import(upgraded);

            var offer = loaded.Offers.Single();
            Assert.Equal(0.20m, offer.Terms.OpeningPrepaymentRate);
            Assert.Equal(NegotiationState.None, offer.Negotiation);
            Assert.Equal(upgraded.Payload, Migration.Upgrade(upgraded).Payload);
        }

        [Fact]
        public void NegotiationState_SurvivesPersistence()
        {
            var (world, _) = Offer(SeedWhere(r => r < 0.05m));
            NegotiationService.Attempt(world, "K1", 15, 0, D);

            var loaded = StateCodec.Import(StateCodec.Export(world));

            Assert.Equal(NegotiationState.Succeeded, loaded.Offers.Single().Negotiation);
            Assert.Equal(0.10m, loaded.Offers.Single().Terms.OpeningPrepaymentRate);
        }
    }
}
