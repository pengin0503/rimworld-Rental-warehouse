using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class PaymentTests
    {
        private const string Dest = "Cell_10_10";

        /// <summary>A one-crate 1,000-silver contract: 60 silver of fee for the term, 30% (18) of it prepaid.</summary>
        private static (WorldState World, ContractState Contract) ActiveNormal(params decimal[] values)
        {
            var terms = Terms(ContractKind.Normal, "K1", values.Length == 0 ? new[] { 1000m } : values, prepay: 0.30m);
            var world = World(terms);
            return (world, Activate(world, terms));
        }

        [Fact]
        public void Claims_PayThePrepaymentAtStartThenTheRestAfterReturn()
        {
            var (world, c) = ActiveNormal();
            long active = c.ActiveTick!.Value;

            var prepaid = PaymentService.Claim(world, MapId, Dest, active);
            RunUntil(c, active + 2 * D);
            Assert.False(PaymentService.Claim(world, MapId, Dest, active + 2 * D).Success);
            RunUntil(c, c.EndTick!.Value);
            Assert.False(PaymentService.Claim(world, MapId, Dest, c.EndTick!.Value).Success);
            Assert.True(ReturnService.RequestPod(c, c.EndTick!.Value).Success);
            ReturnService.Dispatch(c, c.Terms.CargoIds, c.EndTick!.Value + 10);
            var rest = PaymentService.Claim(world, MapId, Dest, c.EndTick!.Value + 20);

            Assert.Equal(18m, Assert.Single(prepaid.Effects).Amount);
            Assert.Equal(54m, Assert.Single(rest.Effects).Amount);
            Assert.All(new[] { prepaid, rest }, d => Assert.Equal(EffectKind.SpawnSilver, d.Effects[0].Kind));
            Assert.Equal(72m, c.ClaimedFee);
            Assert.Equal(0m, world.ReceivableBalance());
        }

        [Fact]
        public void SecondClaim_AddsNothing()
        {
            var (world, c) = ActiveNormal();

            var first = PaymentService.Claim(world, MapId, Dest, c.ActiveTick!.Value + 2 * D);
            var second = PaymentService.Claim(world, MapId, Dest, c.ActiveTick!.Value + 2 * D);

            Assert.True(first.Success);
            Assert.False(second.Success);
            Assert.Equal("RW_Reject_NothingToClaim", second.ReasonKey);
            Assert.Empty(second.Effects);
            Assert.NotEqual(first.Effects[0].EffectId, PaymentService.Claim(world, MapId, Dest, 0).Effects.FirstOrDefault()?.EffectId);
        }

        [Fact]
        public void ClaimEffectIds_AreUnique()
        {
            var (world, c) = ActiveNormal();
            var a = PaymentService.Claim(world, MapId, Dest, 0);
            RunUntil(c, c.EndTick!.Value);
            ReturnAll(c, c.EndTick!.Value + 10);
            var b = PaymentService.Claim(world, MapId, Dest, 0);

            Assert.True(a.Success);
            Assert.True(b.Success);
            Assert.NotEqual(a.Effects[0].EffectId, b.Effects[0].EffectId);
        }

        [Fact]
        public void InvalidDestination_KeepsBalance()
        {
            var (world, c) = ActiveNormal();

            var d = PaymentService.Claim(world, MapId, "", 0);

            Assert.False(d.Success);
            Assert.Equal("RW_Reject_InvalidDestination", d.ReasonKey);
            Assert.Equal(18m, world.ReceivableBalance(MapId));
        }

        [Fact]
        public void Claim_OnlyTakesBalanceOfThatMap()
        {
            var (world, c) = ActiveNormal();

            Assert.False(PaymentService.Claim(world, "Map_99", Dest, 0).Success);
            Assert.Equal(18m, world.ReceivableBalance(MapId));
        }

        [Fact]
        public void BalanceOfVanishedMap_IsClaimedOnceAtAnotherBase()
        {
            var (world, c) = ActiveNormal();
            RunUntil(c, c.ActiveTick!.Value + 2 * D);
            world.RemovedMaps[MapId] = MapRemovalCause.Unknown;
            var snap = new WorldSnapshot { MapIds = { "Map_1" }, FactionIds = { Depositor, Pursuer } };
            foreach (var kv in world.RemovedMaps) snap.RemovedMapCauses[kv.Key] = kv.Value;
            LifecycleService.Reconcile(world, snap, c.ActiveTick!.Value + 2 * D);
            Assert.Equal(ContractPhase.SystemCancelled, c.Phase);
            Assert.Equal(20m, world.ClaimableBalance("Map_1"));

            var first = PaymentService.Claim(world, "Map_1", Dest, 0);
            var second = PaymentService.Claim(world, "Map_1", Dest, 0);

            Assert.True(first.Success);
            Assert.Equal(20m, first.Effects.Single().Amount);
            Assert.Equal("Map_1", first.Effects.Single().EntityId);
            Assert.False(second.Success);
            Assert.Equal(0m, world.ReceivableBalance());
        }

        [Fact]
        public void BalancesOfExistingBases_StaySeparate()
        {
            var a = Terms(ContractKind.Normal, "K1", new[] { 1000m }, prepay: 0.30m);
            var b = Terms(ContractKind.Normal, "K2", new[] { 1000m }, prepay: 0.30m);
            var world = World(a, b);
            var ca = Activate(world, a);
            var cb = Activate(world, b);
            cb.MapId = "Map_1";

            var d = PaymentService.Claim(world, "Map_1", Dest, 0);

            Assert.Equal(18m, d.Effects.Single().Amount);
            Assert.Equal(18m, world.ReceivableBalance(MapId));
            Assert.Equal(0m, world.ClaimableBalance("Map_1"));
        }

        [Fact]
        public void SettlementDebt_IsRecordedOnce()
        {
            var (world, c) = ActiveNormal(1000m, 1000m);
            RunUntil(c, c.ActiveTick!.Value + 4 * D);
            ContractEngine.MarkLost(c, c.Terms.CargoIds[1], LossCause.Lost, c.ActiveTick!.Value + 4 * D);
            RunUntil(c, c.EndTick!.Value);
            ReturnService.RequestPod(c, c.EndTick!.Value);
            ReturnService.Dispatch(c, new[] { c.Terms.CargoIds[0] }, c.EndTick!.Value + 5);

            PaymentService.SyncDebts(world, c.EndTick!.Value + 6);
            PaymentService.SyncDebts(world, c.EndTick!.Value + 7);

            var debt = Assert.Single(world.Debts);
            Assert.Equal(34m, debt.Amount);
            Assert.Equal(Depositor, debt.FactionId);
            Assert.Equal("K1", debt.ContractId);
        }

        [Fact]
        public void RefusingDebt_CostsTwentyGoodwillAndBlocksFactionFifteenDays()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "Debt-K1", ContractId = "K1", FactionId = Depositor, Amount = 34m });

            var d = PaymentService.ResolveDebt(world, "Debt-K1", refuse: true, now: 10 * D);

            Assert.True(d.Success);
            Assert.Empty(world.Debts);
            var goodwill = Assert.Single(d.Effects, e => e.Kind == EffectKind.ApplyGoodwill);
            Assert.Equal(-20m, goodwill.Amount);
            Assert.Equal(Depositor, goodwill.EntityId);
            Assert.Equal(25 * D, world.FactionBlockedUntil[Depositor]);
            Assert.False(PaymentService.ResolveDebt(world, "Debt-K1", refuse: true, now: 10 * D).Success);
        }

        [Fact]
        public void PayingDebt_ConsumesSilverOnce()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "Debt-K1", ContractId = "K1", FactionId = Depositor, Amount = 34m });

            var d = PaymentService.ResolveDebt(world, "Debt-K1", refuse: false, now: 10 * D, mapId: MapId);

            var pay = Assert.Single(d.Effects);
            Assert.Equal(EffectKind.ConsumeSilver, pay.Kind);
            Assert.Equal(34m, pay.Amount);
            Assert.Equal(MapId, pay.EntityId);
            Assert.True(Assert.Single(world.Debts).PaymentPending);
            PaymentService.CompletePayment(world, pay);
            Assert.Empty(world.Debts);
            Assert.False(world.FactionBlockedUntil.ContainsKey(Depositor));
        }

        [Fact]
        public void OnlyOneReturnPodAtATime_ReRequestAfterLossKeepsDeadline()
        {
            var (world, c) = ActiveNormal();
            RunUntil(c, c.EndTick!.Value);
            long deadline = c.ReturnDeadlineTick!.Value;

            var first = ReturnService.RequestPod(c, c.EndTick!.Value + 1);
            var second = ReturnService.RequestPod(c, c.EndTick!.Value + 2);
            ReturnService.NotifyPodLost(c, c.ActiveReturnPodId!);
            var third = ReturnService.RequestPod(c, c.EndTick!.Value + 3);

            Assert.True(first.Success);
            Assert.Equal(EffectKind.CreateReturnPod, Assert.Single(first.Effects).Kind);
            Assert.False(second.Success);
            Assert.Equal("RW_Reject_PodAlreadyActive", second.ReasonKey);
            Assert.True(third.Success);
            Assert.NotEqual(first.Effects[0].EffectId, third.Effects[0].EffectId);
            Assert.Equal(deadline, c.ReturnDeadlineTick);
        }

        [Fact]
        public void LoadedButNotDispatched_IsNotReturned()
        {
            var (world, c) = ActiveNormal();
            RunUntil(c, c.EndTick!.Value);
            ReturnService.RequestPod(c, c.EndTick!.Value);

            RunUntil(c, c.ReturnDeadlineTick!.Value);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(LossCause.Lost, c.CargoStates[0].Loss);
            Assert.Equal(SettlementCause.TotalLoss, c.SettlementCause);
        }

        [Fact]
        public void PartialDispatch_ThenDeadline_SettlesRestAsLoss()
        {
            var (world, c) = ActiveNormal(600m, 600m);
            RunUntil(c, c.EndTick!.Value);
            ReturnService.RequestPod(c, c.EndTick!.Value);

            ReturnService.Dispatch(c, new[] { c.Terms.CargoIds[0] }, c.EndTick!.Value + 5);
            Assert.Equal(ContractPhase.Returning, c.Phase);
            Assert.Null(c.ActiveReturnPodId);
            var effects = RunUntil(c, c.ReturnDeadlineTick!.Value);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Contains(effects, e => e.Kind == EffectKind.RemoveCargo && e.EntityId == c.Terms.CargoIds[1]);
            Assert.Equal(0.5m, c.Settlement!.ReturnValueRate);
        }

        [Fact]
        public void Dispatch_WithoutPod_IsRejected()
        {
            var (world, c) = ActiveNormal();
            RunUntil(c, c.EndTick!.Value);

            Assert.Equal("RW_Reject_NoReturnPod", ReturnService.Dispatch(c, c.Terms.CargoIds, c.EndTick!.Value).ReasonKey);
        }

        [Fact]
        public void Dispatch_IgnoresForeignOrAlreadyHandledCrates()
        {
            var (world, c) = ActiveNormal(600m, 600m);
            RunUntil(c, c.EndTick!.Value);
            ReturnService.RequestPod(c, c.EndTick!.Value);

            var d = ReturnService.Dispatch(c, new[] { c.Terms.CargoIds[0], "other-crate" }, c.EndTick!.Value + 1);

            Assert.True(d.Success);
            Assert.True(c.Cargo(c.Terms.CargoIds[0])!.Returned);
            Assert.False(c.Cargo(c.Terms.CargoIds[1])!.Returned);
        }

        [Fact]
        public void EarlyReturnByPlayer_ExistingLossStillCompensated()
        {
            var (world, c) = ActiveNormal(1000m, 1000m);
            long active = c.ActiveTick!.Value;
            RunUntil(c, active + D);
            ContractEngine.MarkLost(c, c.Terms.CargoIds[1], LossCause.Lost, active + D);

            ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, active + D);
            ReturnService.RequestPod(c, active + D);
            ReturnService.Dispatch(c, new[] { c.Terms.CargoIds[0] }, active + D + 10);

            Assert.Equal(ContractPhase.Settled, c.Phase);
            Assert.Equal(0m, c.Settlement!.CompletionBonus);
            Assert.True(c.Settlement.Compensation > 0m);
        }
    }
}
