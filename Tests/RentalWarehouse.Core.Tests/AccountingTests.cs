using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class AccountingTests
    {
        [Fact]
        public void NormalSixDays_CreditsSeventyTwo()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);

            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 1000);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(60m, c.Settlement!.AccruedFee);
            Assert.Equal(12m, c.Settlement.CompletionBonus);
            Assert.Equal(72m, c.Settlement.Credit);
            Assert.Equal(0m, c.Settlement.Debt);
            Assert.Equal(72m, c.Receivable);
            Assert.Equal(Limits.CompletionGoodwill, c.Settlement.GoodwillDelta);
        }

        [Fact]
        public void NoPrepayment_PaysEverythingAfterTheCratesAreReturned()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            Assert.Equal(0m, c.Receivable);
            RunUntil(c, active + 2 * D);
            RunUntil(c, active + 4 * D);
            RunUntil(c, c.EndTick!.Value);
            Assert.Equal(0m, c.Receivable);

            ReturnAll(c, c.EndTick!.Value + 10);
            Assert.Equal(72m, c.Receivable);
        }

        [Fact]
        public void Prepayment_ArrivesWhenTheTermStarts_TheRestAfterReturn_TotalUnchanged()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.25m);
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            Assert.Equal(15m, c.Receivable);
            RunUntil(c, active + 2 * D);
            RunUntil(c, active + 4 * D);
            RunUntil(c, c.EndTick!.Value);
            Assert.Equal(15m, c.Receivable);

            ReturnAll(c, c.EndTick!.Value + 10);
            Assert.Equal(72m, c.Receivable);
            Assert.Equal(72m, c.Settlement!.Credit);
        }

        [Fact]
        public void Prepayment_IsPostedOnceAtTermStart_WithOneNotice()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.25m);
            var world = World(terms);
            var c = ActivateWithEffects(world, terms, out var startEffects);

            var notice = Assert.Single(startEffects, e => e.Kind == EffectKind.Notify);
            Assert.Equal("RW_Msg_PrepaymentPosted", notice.Payload["key"]);
            Assert.Equal(15m, notice.Amount);
            Assert.Equal(15m, c.PostedFee);

            var later = RunUntil(c, c.EndTick!.Value);
            Assert.DoesNotContain(later, e => e.EffectId == notice.EffectId);
            Assert.Equal(15m, c.PostedFee);
        }

        [Fact]
        public void NoPrepayment_PostsNoNotice()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);

            ActivateWithEffects(world, terms, out var startEffects);

            Assert.DoesNotContain(startEffects, e => e.Kind == EffectKind.Notify);
        }

        [Theory]
        [InlineData(0.00, 0)]
        [InlineData(0.01, 0)]
        [InlineData(0.30, 18)]
        public void Prepayment_IsAWholeNumberOfSilverRoundedDownFromTheFullTermFee(double rate, int expected)
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: (decimal)rate);

            Assert.Equal(60m, Accounting.FullTermFee(terms));
            Assert.Equal(expected, Accounting.Prepayment(terms));
        }

        [Fact]
        public void Prepayment_IgnoresTheCompletionBonus_AndScalesWithTheTerm()
        {
            var shortTerm = Terms(ContractKind.Normal, "K1", new[] { 1000m }, term: 3 * D, prepay: 0.30m);
            var longTerm = Terms(ContractKind.Normal, "K2", new[] { 1000m }, term: 12 * D, prepay: 0.30m);

            Assert.Equal(30m, Accounting.FullTermFee(shortTerm));
            Assert.Equal(9m, Accounting.Prepayment(shortTerm));
            Assert.Equal(120m, Accounting.FullTermFee(longTerm));
            Assert.Equal(36m, Accounting.Prepayment(longTerm));
        }

        [Fact]
        public void EarlyReturnAfterClaimingThePrepayment_OwesTheUnearnedPart()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.30m);
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;
            Assert.True(PaymentService.Claim(world, MapId, "Cell_1_1", active).Success);

            RunUntil(c, active + D);
            ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, active + D);
            ReturnAll(c, active + D + 10);

            // 10 earned, all of it taken as cancellation fee, 18 already paid out.
            Assert.Equal(18m, c.Settlement!.PreviouslyClaimed);
            Assert.Equal(10m, c.Settlement.CancellationFee);
            Assert.Equal(18m, c.Settlement.Debt);
            Assert.Equal(0m, c.Settlement.Credit);
        }

        [Fact]
        public void EarlyReturnBeforeClaimingThePrepayment_OwesNothing()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.30m);
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + D);
            ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, active + D);
            ReturnAll(c, active + D + 10);

            Assert.Equal(0m, c.Settlement!.Debt);
            Assert.Equal(0m, c.Receivable);
        }

        [Fact]
        public void HighRiskSixDays_TwoRaidsStarted_CreditsTwoHundredFiftyTwo()
        {
            var terms = Terms(ContractKind.HighRisk, "K1", new[] { 4000m }, raids: 2);
            var world = World(terms);
            var c = Activate(world, terms);
            c.StartedRaids = 2;

            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(168m, c.Settlement!.AccruedFee);
            Assert.Equal(84m, c.Settlement.CompletionBonus);
            Assert.Equal(252m, c.Settlement.Credit);
        }

        [Fact]
        public void HighRiskSixDays_OneOfTwoRaidsStarted_CreditsTwoHundredTen()
        {
            var terms = Terms(ContractKind.HighRisk, "K1", new[] { 4000m }, raids: 2);
            var world = World(terms);
            var c = Activate(world, terms);
            c.StartedRaids = 1;

            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(210m, c.Settlement!.Credit);
        }

        [Fact]
        public void NoScheduledRaid_FulfilmentFactorIsOne()
        {
            Assert.Equal(1m, Accounting.RaidFulfilment(scheduled: 0, started: 0));
            Assert.Equal(0.5m, Accounting.RaidFulfilment(scheduled: 2, started: 1));
            Assert.Equal(1m, Accounting.RaidFulfilment(scheduled: 1, started: 3));
        }

        [Fact]
        public void PartialLoss_DebtThirtyFour()
        {
            var c = PartialLossContract(claimFirst: false);

            Assert.Equal(100m, c.Settlement!.AccruedFee);
            Assert.Equal(10m, c.Settlement.CompletionBonus);
            Assert.Equal(144m, c.Settlement.Compensation);
            Assert.Equal(0m, c.Settlement.Credit);
            Assert.Equal(34m, c.Settlement.Debt);
            Assert.Equal(0m, c.Receivable);
            Assert.Equal(-5, c.Settlement.GoodwillDelta);
        }

        [Fact]
        public void PartialLoss_AfterClaimingThePrepayment_DebtSeventy()
        {
            var c = PartialLossContract(claimFirst: true);

            Assert.Equal(36m, c.Settlement!.PreviouslyClaimed);
            Assert.Equal(70m, c.Settlement.Debt);
        }

        private static ContractState PartialLossContract(bool claimFirst)
        {
            // Full-term fee 120, 30% prepaid = 36.
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m }, prepay: 0.30m);
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 4 * D);
            Assert.Equal(36m, c.Receivable);
            if (claimFirst)
            {
                c.ClaimedFee += c.Receivable;
                c.Receivable = 0m;
            }

            ContractEngine.MarkLost(c, terms.CargoIds[1], LossCause.Lost, active + 4 * D);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);
            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(SettlementCause.PartialLoss, c.SettlementCause);
            return c;
        }

        [Fact]
        public void CrateOutsideStorageForOneDay_StopsOnlyThatCratesTenSilver_MaturityUnchanged()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;
            long end = c.EndTick!.Value;
            string moved = terms.CargoIds[0];

            RunUntil(c, end, t => t > active + 1 * D && t <= active + 2 * D ? StoredExcept(c, moved) : StoredAll(c));

            Assert.Equal(end, c.EndTick);
            Assert.Equal(50m, c.Cargo(moved)!.AccruedFee);
            Assert.Equal(60m, c.Cargo(terms.CargoIds[1])!.AccruedFee);
            Assert.Equal(110m, c.AccruedFee);
        }

        [Fact]
        public void Embezzlement_CompensatesFullValueAndReplacesLossPenalty()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 3 * D);
            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Embezzled, active + 3 * D);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            Assert.Equal(1000m, c.Settlement!.Compensation);
            Assert.Equal(Limits.EmbezzleGoodwill, c.Settlement.GoodwillDelta);
        }

        [Fact]
        public void MixedLosses_SumByCause()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 600m, 600m, 600m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 1 * D);
            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Embezzled, active + D);
            ContractEngine.MarkLost(c, terms.CargoIds[1], LossCause.Lost, active + D);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            decimal normalCap = c.Terms.NominalTotalReward;
            Assert.Equal(600m + System.Math.Min(600m * 0.25m, normalCap), c.Settlement!.Compensation);
        }

        [Fact]
        public void EarlyReturnByPlayer_NoCompletionBonus_CancellationFeeCappedAtTwoDays()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 3 * D);
            Assert.True(ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, active + 3 * D).Success);
            ReturnAll(c, active + 3 * D + 10);

            Assert.Equal(30m, c.Settlement!.AccruedFee);
            Assert.Equal(0m, c.Settlement.CompletionBonus);
            Assert.Equal(20m, c.Settlement.CancellationFee);
            Assert.Equal(10m, c.Settlement.Credit);
            Assert.Equal(0, c.Settlement.GoodwillDelta);
        }

        [Fact]
        public void FractionalFeesAreTruncatedOnce_AtTheFinalSettlement()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 777m }, prepay: 0.30m);
            var world = World(terms);
            var c = Activate(world, terms);

            // 8 + 777 * 0.2% = 9.554 per day, 57.324 for the term; 30% of it = 17.1972 -> 17
            Assert.Equal(17m, c.Receivable);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            // 57.324 fee + 11.4648 completion = 68.7888 -> 68, prepayment included
            Assert.Equal(68m, c.Settlement!.Credit);
            Assert.Equal(68m, c.Receivable);
        }

        [Fact]
        public void NominalTotalReward_IsFullTermFullReturnReward()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            Assert.Equal(144m, terms.NominalTotalReward);
            var danger = Terms(ContractKind.HighRisk, "K2", new[] { 4000m }, raids: 2);
            Assert.Equal(252m, danger.NominalTotalReward);
        }

        [Fact]
        public void FeeMultiplierScalesDailyFee()
        {
            var cargo = new[] { ("K1-C0", 1000m) }.ToList();
            var settings = new WarehouseSettings { FeeMultiplier = 2.0m };
            var terms = ContractTermsFactory.Create(ContractParameters.Normal, "K1", Depositor, Pursuer, cargo, 0, Term, 0m, settings, 0, 1, 0);
            Assert.Equal(20m, Accounting.DailyFee(terms, 1000m));
            Assert.Equal(144m, terms.NominalTotalReward);
        }
    }
}
