using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class PersistenceTests
    {
        private static WorldState Busy()
        {
            var a = Terms(ContractKind.Normal, "K1", new[] { 777m, 1234.5m });
            var b = Terms(ContractKind.HighRisk, "K2", new[] { 3000m }, raids: 2);
            var world = World(a, b);
            world.WorldSeed = -98765;
            world.BusinessStartTick = 123;
            world.NextOfferTick = 456789012345L;
            world.FactionBlockedUntil["Faction_7"] = 900000;
            world.Settings.FeeMultiplier = 1.75m;
            var c = Activate(world, a);
            world.Debts.Add(new Debt { DebtId = "Debt-1", ContractId = "K0", FactionId = "Faction_7", Amount = 33m, CreatedTick = 77 });
            RunUntil(c, c.ActiveTick!.Value + 3 * D + 125);
            c.RaidPlan.Add(new RaidSlot { Index = 0, PlannedTick = 5 * D, State = RaidSlotState.Started, StartedTick = 5 * D + 10 });
            world.Cargo[a.CargoIds[0]].Manifest.Add(new ManifestEntry { ThingDefName = "Gun_Revolver", StuffDefName = null, Quality = 4, Count = 1 });
            EffectQueue.Enqueue(world, new EffectRequest { EffectId = "e1", Kind = EffectKind.SpawnSilver, EntityId = MapId, Amount = 12.5m, Payload = { ["k"] = "v" } });
            world.Effects.TryBegin("e0", 5);
            world.Effects.Complete("e0", 6);
            return world;
        }

        [Fact]
        public void RoundTrip_PreservesSeedsTicksAndAmounts()
        {
            var world = Busy();

            var saved = StateCodec.Export(world);
            var loaded = StateCodec.Import(saved);

            Assert.Equal(WorldState.CurrentSchemaVersion, saved.SchemaVersion);
            Assert.Equal(StateCodec.Export(world).Payload, StateCodec.Export(loaded).Payload);
            var c = loaded.Contract("K1")!;
            var original = world.Contract("K1")!;
            Assert.Equal(original.Terms.Seed, c.Terms.Seed);
            Assert.Equal(original.EndTick, c.EndTick);
            Assert.Equal(original.AccruedFee, c.AccruedFee);
            Assert.Equal(original.Receivable, c.Receivable);
            Assert.Equal(1234.5m, c.Terms.CargoValues[1]);
            Assert.Equal(-98765, loaded.WorldSeed);
            Assert.Equal(456789012345L, loaded.NextOfferTick);
            Assert.Equal(1.75m, loaded.Settings.FeeMultiplier);
            Assert.Equal(4, loaded.Cargo["K1-C0"].Manifest.Single(m => m.ThingDefName == "Gun_Revolver").Quality);
            Assert.Equal("v", loaded.PendingEffects.Single().Payload["k"]);
            Assert.True(loaded.Effects.IsCompleted("e0"));
            Assert.Equal(RaidSlotState.Started, c.RaidPlan[0].State);
            Assert.Equal(33m, loaded.Debts[0].Amount);
        }

        [Fact]
        public void RoundTrip_ThenContinuing_BehavesLikeOriginal()
        {
            var world = Busy();
            var loaded = StateCodec.Import(StateCodec.Export(world));

            var a = world.Contract("K1")!;
            var b = loaded.Contract("K1")!;
            RunUntil(a, a.EndTick!.Value);
            RunUntil(b, b.EndTick!.Value);
            ReturnAll(a, a.EndTick!.Value + 1);
            ReturnAll(b, b.EndTick!.Value + 1);

            Assert.Equal(a.Settlement!.Credit, b.Settlement!.Credit);
            Assert.Equal(a.Receivable, b.Receivable);
        }

        [Fact]
        public void Import_GarbagePayload_FailsWithReason()
        {
            var bad = new SavedState { SchemaVersion = 1, Payload = "{not json" };

            var ex = Assert.Throws<StateCodecException>(() => StateCodec.Import(bad));
            Assert.Contains("RW_Diag_LedgerUnreadable", ex.ReasonKey);
        }

        [Fact]
        public void Import_EmptyPayload_ReturnsFreshState()
        {
            var fresh = StateCodec.Import(new SavedState { SchemaVersion = 0, Payload = "" });

            Assert.Empty(fresh.Contracts);
            Assert.Equal(WorldState.CurrentSchemaVersion, fresh.SchemaVersion);
        }
    }
}
