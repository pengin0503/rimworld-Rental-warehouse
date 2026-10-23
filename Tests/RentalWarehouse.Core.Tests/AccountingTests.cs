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
        public void NormalSixDays_PaysTwentyTwentyThenThirtyTwo()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 2 * D);
            Assert.Equal(20m, c.Receivable);
            RunUntil(c, active + 4 * D);
            Assert.Equal(40m, c.Receivable);
            RunUntil(c, c.EndTick!.Value);
            Assert.Equal(40m, c.Receivable);

            ReturnAll(c, c.EndTick!.Value + 10);
            Assert.Equal(72m, c.Receivable);
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
        public void PartialLoss_AfterClaimingEighty_DebtOneHundredFourteen()
        {
            var c = PartialLossContract(claimFirst: true);

            Assert.Equal(80m, c.Settlement!.PreviouslyClaimed);
            Assert.Equal(114m, c.Settlement.Debt);
        }

        private static ContractState PartialLossContract(bool claimFirst)
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            RunUntil(c, active + 4 * D);
            Assert.Equal(80m, c.Receivable);
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
        public void FractionalFeesCarryOverAndFinalRemainderIsTruncated()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 777m });
            var world = World(terms);
            var c = Activate(world, terms);
            long active = c.ActiveTick!.Value;

            // 8 + 777 * 0.2% = 9.554 per day
            RunUntil(c, active + 2 * D);
            Assert.Equal(19m, c.Receivable);
            RunUntil(c, active + 4 * D);
            Assert.Equal(38m, c.Receivable);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);

            // 57.324 fee + 11.4648 completion = 68.7888 -> 68
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
            var terms = ContractTermsFactory.Create(ContractParameters.Normal, "K1", Depositor, Pursuer, cargo, 0, settings, 0, 1, 0);
            Assert.Equal(20m, Accounting.DailyFee(terms, 1000m));
            Assert.Equal(144m, terms.NominalTotalReward);
        }
    }
}
