using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class ReviewRegressionTests
    {
        [Theory]
        [InlineData(EffectKind.SpawnSilver)]
        [InlineData(EffectKind.ConsumeSilver)]
        [InlineData(EffectKind.ConsumeCargo)]
        [InlineData(EffectKind.DeliverCargo)]
        public void DurableRetryCooldownSurvivesSaveLoad(EffectKind kind)
        {
            var world = new WorldState();
            EffectQueue.Enqueue(world, new EffectRequest { EffectId = "x", Kind = kind });
            for (int i = 0; i < 5; i++) EffectQueue.Process(world, _ => throw new System.InvalidOperationException(), now: i * 250);
            long next = world.Effects.Records["x"].NextAttemptTick;
            var loaded = StateCodec.Import(StateCodec.Export(world));
            int calls = 0;
            EffectQueue.Process(loaded, _ => { calls++; return true; }, now: next - 1);
            Assert.Equal(0, calls);
            Assert.Single(loaded.PendingEffects);
            EffectQueue.Process(loaded, _ => { calls++; return true; }, now: next);
            Assert.Equal(1, calls);
            Assert.Empty(loaded.PendingEffects);
        }

        [Fact]
        public void PendingPaymentLockSurvivesSaveLoad()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "D", ContractId = "K", FactionId = Depositor, Amount = 100 });
            EffectQueue.EnqueueAll(world, PaymentService.ResolveDebt(world, "D", false, 0, MapId).Effects);
            var loaded = StateCodec.Import(StateCodec.Export(world));
            Assert.True(loaded.Debts.Single().PaymentPending);
            Assert.Equal(EffectKind.ConsumeSilver, loaded.PendingEffects.Single().Kind);
            Assert.False(PaymentService.ResolveDebt(loaded, "D", true, 1).Success);
        }

        [Fact]
        public void FinishedPaymentIdsArePrunedButPendingIdsRemain()
        {
            var world = new WorldState();
            world.Effects.Complete("debt-payment-1", 1);
            world.Effects.TryBegin("debt-payment-2", 1);
            LifecycleService.Reconcile(world, new WorldSnapshot(), 100 * D);
            Assert.False(world.Effects.Records.ContainsKey("debt-payment-1"));
            Assert.True(world.Effects.Records.ContainsKey("debt-payment-2"));
        }

        [Fact]
        public void QuoteAtMaturityUsesMaturedSettlementWithoutMutatingContract()
        {
            var t = Terms(ContractKind.Normal, "K", new[] { 1000m });
            var world = World(t);
            var c = Activate(world, t);
            string before = StateCodec.Export(world).Payload;
            var quote = ContractEngine.PreviewEarlyReturn(c, c.EndTick!.Value);
            Assert.Equal(0m, quote.CancellationFee);
            Assert.Equal(12m, quote.CompletionBonus);
            Assert.Equal(72m, quote.Credit);
            Assert.Equal(before, StateCodec.Export(world).Payload);
        }

        [Fact]
        public void ReopenedPaymentGetsNewEffectIdAndKeepsDebt()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "D", ContractId = "K", FactionId = Depositor, Amount = 100 });
            var first = PaymentService.ResolveDebt(world, "D", false, 0, MapId).Effects.Single();
            PaymentService.ReopenPayment(world, first);
            var second = PaymentService.ResolveDebt(world, "D", false, 1, "Map_1").Effects.Single();
            Assert.NotEqual(first.EffectId, second.EffectId);
            Assert.Equal(100, world.Debts.Single().Amount);
        }

        [Fact]
        public void DebtStaysBlockingUntilPaymentActuallyCompletes()
        {
            var world = new WorldState();
            world.Debts.Add(new Debt { DebtId = "D", ContractId = "K", FactionId = Depositor, Amount = 100 });
            var d = PaymentService.ResolveDebt(world, "D", false, 1, MapId);
            Assert.True(d.Success);
            Assert.Single(world.Debts);
            Assert.True(world.Debts[0].PaymentPending);
            Assert.False(PaymentService.ResolveDebt(world, "D", true, 2).Success);
            Assert.False(PaymentService.ResolveDebt(world, "D", false, 2, MapId).Success);
            PaymentService.CompletePayment(world, d.Effects.Single());
            Assert.Empty(world.Debts);
        }

        [Fact]
        public void EconomicEffectsRemainRetryableAfterRepeatedExceptions()
        {
            var world = new WorldState();
            EffectQueue.Enqueue(world, new EffectRequest { EffectId = "claim-1", Kind = EffectKind.SpawnSilver });
            for (int i = 0; i < 10; i++) EffectQueue.Process(world, _ => throw new System.InvalidOperationException(), now: i * 10000);
            Assert.Single(world.PendingEffects);
            Assert.False(world.Effects.IsAbandoned("claim-1"));
            EffectQueue.Process(world, _ => true, now: 100000);
            Assert.Empty(world.PendingEffects);
        }

        [Fact]
        public void MissingPodRecoversAfterAbandonment_ButNotWhilePending_AndKeepsDeadline()
        {
            var world = new WorldState();
            var c = new ContractState { Terms = Terms(ContractKind.Normal, "K", new[] { 1000m }), Phase = ContractPhase.Returning, ReturnDeadlineTick = 9000 };
            var first = ReturnService.RequestPod(c, 100).Effects.Single();
            EffectQueue.Enqueue(world, first);
            Assert.False(ReturnService.RecoverMissingPod(world, c, exists: false));
            EffectQueue.Process(world, _ => throw new System.InvalidOperationException());
            for (int i = 1; i < EffectQueue.MaxAttempts; i++) EffectQueue.Process(world, _ => throw new System.InvalidOperationException());
            Assert.True(ReturnService.RecoverMissingPod(world, c, exists: false));
            var second = ReturnService.RequestPod(c, 200);
            Assert.True(second.Success);
            Assert.NotEqual(first.EffectId, c.ActiveReturnPodId);
            Assert.Equal(9000, c.ReturnDeadlineTick);
        }

        [Fact]
        public void ConsumedSiteCargoAndOpenEffectArePruned_EvenAfterSiteWasPruned()
        {
            var world = new WorldState();
            var cargo = new CargoRecord { CargoId = "S-0", SiteId = "S", Custody = CargoCustody.Player, Opened = true };
            world.Cargo[cargo.CargoId] = cargo;
            world.Effects.Complete("open:S-0", 1);
            LifecycleService.Reconcile(world, new WorldSnapshot(), 100 * D);
            Assert.False(world.Cargo.ContainsKey("S-0"));
            Assert.False(world.Effects.Records.ContainsKey("open:S-0"));
        }

        [Fact]
        public void CleanupKeepsUnopenedAndPendingSiteCargo()
        {
            var world = new WorldState();
            world.Cargo["unopened"] = new CargoRecord { CargoId = "unopened", SiteId = "S", Custody = CargoCustody.Player };
            world.Cargo["pending"] = new CargoRecord { CargoId = "pending", SiteId = "S", Custody = CargoCustody.Player, Opened = true };
            EffectQueue.Enqueue(world, new EffectRequest { EffectId = "open:pending", EntityId = "pending", Kind = EffectKind.ConsumeCargo });
            LifecycleService.Reconcile(world, new WorldSnapshot(), 100 * D);
            Assert.Equal(2, world.Cargo.Count);
        }

        [Fact]
        public void EarlyReturnPreviewShowsPrepaymentDebtAndTrust_WithoutMutatingLiveState()
        {
            var t = Terms(ContractKind.Normal, "K", new[] { 1000m }, term: 12 * D, prepay: 0.3m);
            var world = World(t);
            var c = Activate(world, t);
            world.TrustPoints = 100;
            var claim = PaymentService.Claim(world, MapId, "Cell_1_1", c.ActiveTick!.Value);
            Assert.Equal(36m, claim.Effects.Single().Amount);
            RunUntil(c, c.ActiveTick.Value + D);
            string before = StateCodec.Export(world).Payload;
            var quote = ContractEngine.PreviewEarlyReturn(c, c.ActiveTick.Value + D);
            Assert.Equal(36m, quote.Debt);
            Assert.Equal(10m, quote.CancellationFee);
            Assert.Equal(-5, quote.TrustDelta);
            Assert.Equal(before, StateCodec.Export(world).Payload);
        }
    }
}
