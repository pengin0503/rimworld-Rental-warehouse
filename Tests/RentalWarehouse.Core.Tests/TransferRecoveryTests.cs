using System;
using System.Collections.Generic;
using RentalWarehouse.Core;
using Xunit;

namespace RentalWarehouse.Core.Tests
{
    public class TransferRecoveryTests
    {
        private sealed class Item { public int Count; public bool Delivered; }

        [Fact]
        public void ExceptionAfterDetachingStillRetainsTheOriginalItem()
        {
            var item = new Item { Count = 10 };
            var pending = new List<Item> { item };
            Assert.Throws<InvalidOperationException>(() => TransferRecovery.Run(pending.ToArray(),
                x => { pending.Remove(x); throw new InvalidOperationException(); }, _ => { }, x => x.Delivered, x => pending.Add(x)));
            Assert.Same(item, Assert.Single(pending));
        }

        [Fact]
        public void PartialHandoffIsRetained_AndAlreadyTransferredItemsAreNotRepeated()
        {
            var pending = new List<Item> { new Item { Count = 10 }, new Item { Count = 20 } };
            int delivered = 0;
            bool first = true;
            void Run() => TransferRecovery.Run(pending.ToArray(), x => pending.Remove(x), x =>
            {
                if (first) { delivered += 4; x.Count -= 4; first = false; throw new InvalidOperationException(); }
                delivered += x.Count; x.Delivered = true;
            }, x => x.Delivered, x => pending.Add(x));
            Assert.Throws<InvalidOperationException>(Run);
            Assert.Equal(26, pending[0].Count + pending[1].Count);
            Run();
            Assert.Empty(pending);
            Assert.Equal(30, delivered);
            Run();
            Assert.Equal(30, delivered);
        }

        [Fact]
        public void ExceptionAfterSuccessfulHandoffDoesNotRetainOrDuplicateTheItem()
        {
            var pending = new List<Item> { new Item { Count = 10 } };
            Assert.Throws<InvalidOperationException>(() => TransferRecovery.Run(pending.ToArray(), x => pending.Remove(x),
                x => { x.Delivered = true; throw new InvalidOperationException(); }, x => x.Delivered, x => pending.Add(x)));
            Assert.Empty(pending);
        }

        [Fact]
        public void RejectedPlacementRetainsSameItemAndReturnsIncomplete()
        {
            var item = new Item { Count = 10 };
            var pending = new List<Item> { item };
            Assert.False(TransferRecovery.Run(pending.ToArray(), x => pending.Remove(x), _ => { }, x => x.Delivered, x => pending.Add(x)));
            Assert.Same(item, Assert.Single(pending));
        }
    }
}
