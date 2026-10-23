using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class ContractTests
    {
        [Fact]
        public void Accept_ReservesSlotsAndSchedulesDeliveryNextDay()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 300m, 300m, 300m, 300m });
            var world = World(terms);

            var d = ContractEngine.Accept(terms, Storage(rackFree: 4), world, 100);

            Assert.True(d.Success, d.ReasonKey);
            var c = world.Contract("K1")!;
            Assert.Equal(ContractPhase.AwaitingDelivery, c.Phase);
            Assert.Equal(100 + D, c.DeliveryTick);
            Assert.Empty(world.Offers);
            Assert.Equal(MapId, c.MapId);
        }

        [Fact]
        public void Accept_TwiceSameOffer_IsRejectedWithoutSecondContract()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            var world = World(terms);

            Assert.True(ContractEngine.Accept(terms, Storage(4), world, 0).Success);
            var again = ContractEngine.Accept(terms, Storage(4), world, 0);

            Assert.False(again.Success);
            Assert.Equal("RW_Reject_NotOffered", again.ReasonKey);
            Assert.Single(world.Contracts);
        }

        [Fact]
        public void Accept_DoubleReservation_IsRejected()
        {
            var a = Terms(ContractKind.Normal, "K1", new[] { 300m, 300m, 300m });
            var b = Terms(ContractKind.Normal, "K2", new[] { 300m, 300m });
            var world = World(a, b);

            Assert.True(ContractEngine.Accept(a, Storage(rackFree: 4), world, 0).Success);
            var second = ContractEngine.Accept(b, Storage(rackFree: 4), world, 0);

            Assert.False(second.Success);
            Assert.Equal("RW_Reject_NoCapacity", second.ReasonKey);
        }

        [Fact]
        public void Accept_VaultCargo_DoesNotFitInRacks()
        {
            var terms = Terms(ContractKind.HighRisk, "K1", new[] { 3000m });
            var world = World(terms);

            var d = ContractEngine.Accept(terms, Storage(rackFree: 4, vaultFree: 0), world, 0);

            Assert.False(d.Success);
            Assert.Equal("RW_Reject_NoCapacity", d.ReasonKey);
        }

        [Fact]
        public void Accept_NormalCargo_MayUseVaultSlots()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 300m, 300m });
            var world = World(terms);

            Assert.True(ContractEngine.Accept(terms, Storage(rackFree: 0, vaultFree: 2), world, 0).Success);
        }

        [Fact]
        public void Accept_FourthConcurrentContract_IsRejected()
        {
            var world = World();
            for (int i = 0; i < 4; i++) AddOffer(world, Terms(ContractKind.Normal, "K" + i, new[] { 500m }));

            for (int i = 0; i < 3; i++) Assert.True(ContractEngine.Accept(world.Offers[0].Terms, Storage(4), world, 0).Success);
            var fourth = ContractEngine.Accept(world.Offers[0].Terms, Storage(4), world, 0);

            Assert.False(fourth.Success);
            Assert.Equal("RW_Reject_TooManyContracts", fourth.ReasonKey);
        }

        [Fact]
        public void Accept_NegativeOrInvalidTerms_AreRejected()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            terms.CargoValues[0] = -1m;
            var world = World(terms);

            var d = ContractEngine.Accept(terms, Storage(4), world, 0);

            Assert.False(d.Success);
            Assert.Equal("RW_Reject_InvalidTerms", d.ReasonKey);
            Assert.Empty(world.Contracts);
        }

        [Fact]
        public void Accept_WhileInDebt_IsRejected()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            var world = World(terms);
            world.Debts.Add(new Debt { DebtId = "D1", FactionId = "Faction_9", Amount = 5m });

            var d = ContractEngine.Accept(terms, Storage(4), world, 0);

            Assert.False(d.Success);
            Assert.Equal("RW_Reject_InDebt", d.ReasonKey);
        }

        [Fact]
        public void Accept_BlockedFaction_IsRejectedUntilBlockEnds()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            var world = World(terms);
            world.FactionBlockedUntil[Depositor] = 10 * D;
            terms.OfferExpiresTick = 20 * D;

            Assert.Equal("RW_Reject_FactionBlocked", ContractEngine.Accept(terms, Storage(4), world, 5 * D).ReasonKey);
            Assert.True(ContractEngine.Accept(terms, Storage(4), world, 10 * D).Success);
        }

        [Fact]
        public void Accept_ExpiredOffer_IsRejected()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            var world = World(terms);

            Assert.Equal("RW_Reject_OfferExpired", ContractEngine.Accept(terms, Storage(4), world, terms.OfferExpiresTick).ReasonKey);
        }

        [Fact]
        public void Delivery_EmitsOneEffectPerCrateOnce()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m, 500m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;

            var before = ContractEngine.Advance(c, Storage(4), D - 1);
            var at = ContractEngine.Advance(c, Storage(4), D);
            var again = ContractEngine.Advance(c, Storage(4), D + 250);

            Assert.Empty(before.Effects);
            Assert.Equal(2, at.Effects.Count(e => e.Kind == EffectKind.DeliverCargo));
            Assert.Equal(2, again.Effects.Count(e => e.Kind == EffectKind.DeliverCargo));
            Assert.Equal(at.Effects.Select(e => e.EffectId), again.Effects.Select(e => e.EffectId));
        }

        [Fact]
        public void Storage_PartiallyStored_DoesNotStartTerm()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m, 500m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            foreach (var id in terms.CargoIds) ContractEngine.MarkDelivered(c, id, D);

            ContractEngine.Advance(c, StoredExcept(c, terms.CargoIds[1]), D + 1000);

            Assert.Equal(ContractPhase.AwaitingStorage, c.Phase);
            Assert.Null(c.ActiveTick);
        }

        [Fact]
        public void Storage_NotCompletedWithinOneDay_MovesToEarlyReturnWithoutIncome()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            ContractEngine.MarkDelivered(c, terms.CargoIds[0], D);

            ContractEngine.Advance(c, Storage(4), 2 * D);

            Assert.Equal(ContractPhase.Returning, c.Phase);
            Assert.Equal(ReturnReason.StorageFailed, c.ReturnReason);
            Assert.Equal(2 * D + Limits.ReturnGrace, c.ReturnDeadlineTick);

            ReturnAll(c, 2 * D + 100);
            Assert.Equal(0m, c.Settlement!.Credit);
            Assert.Equal(0m, c.Settlement.Debt);
            Assert.Equal(0, c.Settlement.GoodwillDelta);
        }

        [Fact]
        public void PreStorageLoss_OfOneCrate_MovesToEarlyReturnAndStillCompensates()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 300m, 300m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            foreach (var id in terms.CargoIds) ContractEngine.MarkDelivered(c, id, D);

            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, D + 100);

            Assert.Equal(ContractPhase.Returning, c.Phase);
            Assert.Equal(ReturnReason.StorageFailed, c.ReturnReason);
            ReturnAll(c, D + 200);
            Assert.Equal(75m, c.Settlement!.Compensation);
            Assert.Equal(75m, c.Settlement.Debt);
        }

        [Fact]
        public void PreStorageLoss_OfAllCrates_SettlesImmediately()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 800m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            ContractEngine.MarkDelivered(c, terms.CargoIds[0], D);

            var d = ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, D + 100);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(SettlementCause.TotalLoss, c.SettlementCause);
            Assert.Contains(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
        }

        [Fact]
        public void ActiveTotalLoss_SettlesImmediately()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);

            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, c.ActiveTick!.Value + D);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(10m, c.Settlement!.AccruedFee);
        }

        [Fact]
        public void Maturity_IsFixedAtActivation()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long end = c.EndTick!.Value;

            RunUntil(c, end - D, _ => Storage(4));
            RunUntil(c, end);

            Assert.Equal(c.ActiveTick + 6 * D, end);
            Assert.Equal(end, c.EndTick);
            Assert.Equal(ContractPhase.Returning, c.Phase);
            Assert.Equal(ReturnReason.Matured, c.ReturnReason);
            Assert.Equal(end + Limits.ReturnGrace, c.ReturnDeadlineTick);
        }

        [Fact]
        public void FeesDoNotGrowDuringReturnGrace()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);

            RunUntil(c, c.EndTick!.Value + 2 * D);

            Assert.Equal(60m, c.AccruedFee);
        }

        [Fact]
        public void ReturnGraceExpiry_SettlesUnreturnedAsLossAndRemovesCrates()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            RunUntil(c, c.EndTick!.Value);
            ContractEngine.MarkReturned(c, terms.CargoIds[0], c.EndTick!.Value + 10);

            var effects = RunUntil(c, c.ReturnDeadlineTick!.Value);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(LossCause.Lost, c.Cargo(terms.CargoIds[1])!.Loss);
            Assert.Contains(effects, e => e.Kind == EffectKind.RemoveCargo && e.EntityId == terms.CargoIds[1]);
        }

        [Fact]
        public void Cancel_BeforeArrival_IsPenaltyFree()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;

            var d = ContractEngine.Cancel(c, D - 1);

            Assert.True(d.Success);
            Assert.Equal(ContractPhase.Cancelled, c.Phase);
            Assert.DoesNotContain(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
            Assert.False(ContractEngine.Cancel(c, D - 1).Success);
        }

        [Fact]
        public void Cancel_AfterArrival_IsNotAllowed()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);

            Assert.Equal("RW_Reject_AlreadyArrived", ContractEngine.Cancel(c, c.ActiveTick!.Value + 1).ReasonKey);
        }

        [Fact]
        public void MarkLost_Twice_DoesNotDoubleCount()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m, 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            long t = c.ActiveTick!.Value + D;

            ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, t);
            var again = ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Embezzled, t);

            Assert.False(again.Success);
            Assert.Equal(LossCause.Lost, c.Cargo(terms.CargoIds[0])!.Loss);
        }

        [Fact]
        public void Settlement_GoodwillEffect_HasStableId()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);
            RunUntil(c, c.EndTick!.Value);

            var d = ContractEngine.MarkReturned(c, terms.CargoIds[0], c.EndTick!.Value + 1);

            var goodwill = Assert.Single(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
            Assert.Equal("K1:settle:goodwill", goodwill.EffectId);
            Assert.Equal(3m, goodwill.Amount);
            Assert.Equal(Depositor, goodwill.EntityId);
        }

        [Fact]
        public void Maturity_IsNotifiedOnce()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 1000m });
            var world = World(terms);
            var c = Activate(world, terms);

            var effects = RunUntil(c, c.EndTick!.Value + D);

            var notice = Assert.Single(effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_Matured");
            Assert.Equal("K1:matured", notice.EffectId);
        }

        [Fact]
        public void LostCrate_IsNotified_EmbezzlementIsNot()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m, 500m, 500m });
            var world = World(terms);
            var c = Activate(world, terms);

            var lost = ContractEngine.MarkLost(c, terms.CargoIds[0], LossCause.Lost, c.ActiveTick!.Value + 10);
            var embezzled = ContractEngine.MarkLost(c, terms.CargoIds[1], LossCause.Embezzled, c.ActiveTick!.Value + 20);

            Assert.Contains(lost.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_CrateLost");
            Assert.DoesNotContain(embezzled.Effects, e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_CrateLost");
        }

        [Fact]
        public void FeeStop_IsNotifiedOncePerCrate_EvenWhenItRepeats()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m, 500m });
            var world = World(terms);
            var c = Activate(world, terms);
            long a = c.ActiveTick!.Value;
            string moved = terms.CargoIds[0];

            var effects = RunUntil(c, a + 3 * D, t => (t / 2000) % 2 == 0 ? StoredExcept(c, moved) : StoredAll(c));

            var stops = effects.Where(e => e.Kind == EffectKind.Notify && e.Payload["key"] == "RW_Msg_FeeStopped").ToList();
            Assert.NotEmpty(stops);
            Assert.All(stops, e => Assert.Equal("K1:feestop:" + moved, e.EffectId));
            Assert.DoesNotContain(effects, e => e.EffectId == "K1:feestop:" + terms.CargoIds[1]);
        }
    }
}
