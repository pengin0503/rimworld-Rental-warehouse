using System;
using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class EffectTests
    {
        private static EffectRequest Silver(string id, decimal amount) =>
            new EffectRequest { EffectId = id, Kind = EffectKind.SpawnSilver, EntityId = MapId, Amount = amount };

        [Fact]
        public void SameEffectIdTwice_AppliesOnce()
        {
            var world = new WorldState();
            int applied = 0;

            EffectQueue.Enqueue(world, Silver("claim-1", 50m));
            EffectQueue.Enqueue(world, Silver("claim-1", 50m));
            EffectQueue.Process(world, e => { applied++; return true; });
            EffectQueue.Enqueue(world, Silver("claim-1", 50m));
            EffectQueue.Process(world, e => { applied++; return true; });

            Assert.Equal(1, applied);
            Assert.True(world.Effects.IsCompleted("claim-1"));
            Assert.Empty(world.PendingEffects);
        }

        [Fact]
        public void Ledger_TryBeginFalseAfterComplete()
        {
            var ledger = new EffectLedger();

            Assert.True(ledger.TryBegin("x", 10));
            Assert.True(ledger.TryBegin("x", 20));
            ledger.Complete("x", 30);

            Assert.False(ledger.TryBegin("x", 40));
            Assert.Equal(2, ledger.Attempts("x"));
        }

        [Fact]
        public void ExceptionDuringDelivery_RetriesOnlyUndeliveredCrates()
        {
            var terms = Terms(ContractKind.Normal, "K1", new[] { 500m, 500m, 500m });
            var world = World(terms);
            ContractEngine.Accept(terms, Storage(4), world, 0);
            var c = world.Contract("K1")!;
            EffectQueue.EnqueueAll(world, ContractEngine.Advance(c, Storage(4), D).Effects);
            var spawned = new List<string>();
            bool fail = true;

            bool Apply(EffectRequest e)
            {
                if (spawned.Contains(e.EntityId)) return true; // reconciled: the crate already exists
                if (e.EntityId == terms.CargoIds[1] && fail) throw new InvalidOperationException("injected");
                spawned.Add(e.EntityId);
                return true;
            }

            EffectQueue.Process(world, Apply);
            Assert.Equal(2, spawned.Count);
            Assert.Single(world.PendingEffects);

            // The contract keeps asking for deliveries until they are confirmed; duplicates are ignored.
            EffectQueue.EnqueueAll(world, ContractEngine.Advance(c, Storage(4), D + 250).Effects);
            fail = false;
            EffectQueue.Process(world, Apply);

            Assert.Equal(terms.CargoIds.OrderBy(x => x), spawned.OrderBy(x => x));
            Assert.Empty(world.PendingEffects);
        }

        [Fact]
        public void HandlerReturningFalse_StaysQueuedForRetry()
        {
            var world = new WorldState();
            EffectQueue.Enqueue(world, Silver("claim-1", 50m));

            EffectQueue.Process(world, _ => false);
            Assert.Single(world.PendingEffects);
            Assert.False(world.Effects.IsCompleted("claim-1"));

            EffectQueue.Process(world, _ => true);
            Assert.Empty(world.PendingEffects);
        }

        [Fact]
        public void ProcessingOrder_IsQueueOrder()
        {
            var world = new WorldState();
            var order = new List<string>();
            EffectQueue.Enqueue(world, Silver("a", 1m));
            EffectQueue.Enqueue(world, Silver("b", 1m));
            EffectQueue.Enqueue(world, Silver("c", 1m));

            EffectQueue.Process(world, e => { order.Add(e.EffectId); return true; });

            Assert.Equal(new[] { "a", "b", "c" }, order);
        }

        [Fact]
        public void EffectEnqueuedDuringProcessing_RunsInSamePass()
        {
            var world = new WorldState();
            var order = new List<string>();
            EffectQueue.Enqueue(world, Silver("a", 1m));

            EffectQueue.Process(world, e =>
            {
                order.Add(e.EffectId);
                if (e.EffectId == "a") EffectQueue.Enqueue(world, Silver("b", 1m));
                return true;
            });

            Assert.Equal(new[] { "a", "b" }, order);
        }

        [Fact]
        public void PoisonEffect_IsDroppedAfterMaxAttemptsAndReported()
        {
            var world = new WorldState();
            EffectQueue.Enqueue(world, Silver("bad", 1m));
            var dropped = new List<string>();

            for (int i = 0; i < EffectQueue.MaxAttempts + 2; i++)
            {
                EffectQueue.Process(world, _ => throw new InvalidOperationException("always"), dropped.Add);
            }

            Assert.Empty(world.PendingEffects);
            Assert.Equal(new[] { "bad" }, dropped);
            Assert.False(world.Effects.IsCompleted("bad"));
            Assert.True(world.Effects.IsAbandoned("bad"));
        }

        [Fact]
        public void NestedProcess_DuringHandler_DoesNotCorruptTheQueue()
        {
            var world = new WorldState();
            var order = new List<string>();
            EffectQueue.Enqueue(world, Silver("a", 1m));
            EffectQueue.Enqueue(world, Silver("b", 1m));
            EffectQueue.Enqueue(world, Silver("c", 1m));
            int nested = -1;

            bool Apply(EffectRequest e)
            {
                order.Add(e.EffectId);
                if (e.EffectId == "a")
                {
                    EffectQueue.Enqueue(world, Silver("d", 1m));
                    nested = EffectQueue.Process(world, Apply);
                }
                return true;
            }

            EffectQueue.Process(world, Apply);

            Assert.Equal(0, nested);
            Assert.Equal(new[] { "a", "b", "c", "d" }, order);
            Assert.Empty(world.PendingEffects);
            Assert.All(new[] { "a", "b", "c", "d" }, id => Assert.True(world.Effects.IsCompleted(id)));
        }
    }
}
