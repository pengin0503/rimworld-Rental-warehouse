using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class OfferTests
    {
        private static readonly List<Good> Goods = new List<Good>
        {
            new Good("Steel", null, 1.9m, 0.5m),
            new Good("Cloth", null, 1.5m, 0.026m),
            new Good("Silver", null, 1m, 0.008m),
            new Good("Gold", null, 10m, 0.008m),
            new Good("ComponentIndustrial", null, 32m, 0.6m),
            new Good("Gun_AssaultRifle", null, 345m, 3.5m, quality: 2),
        };

        private static OfferContext Context(bool withPursuer = true) => new OfferContext
        {
            Goods = Goods,
            Factions =
            {
                new FactionInfo { FactionId = "Faction_1", Humanlike = true, HostileToPlayer = false },
                new FactionInfo { FactionId = "Faction_3", Humanlike = true, HostileToPlayer = false },
                new FactionInfo { FactionId = "Faction_9", Humanlike = false, HostileToPlayer = true, HostileTo = { "Faction_1", "Faction_3" } },
                withPursuer
                    ? new FactionInfo { FactionId = "Faction_2", Humanlike = true, HostileToPlayer = true, HostileTo = { "Faction_1", "Faction_3" } }
                    : new FactionInfo { FactionId = "Faction_2", Humanlike = true, HostileToPlayer = true },
            },
        };

        private static WorldState OpenWorld()
        {
            var world = new WorldState { WorldSeed = 2024 };
            OfferService.SetBusinessOpen(world, true, 0);
            return world;
        }

        private static List<ContractTerms> RunTo(WorldState world, OfferContext ctx, long until)
        {
            var all = new List<ContractTerms>();
            for (long t = 0; t <= until; t += CoreTime.CheckIntervalTicks) all.AddRange(OfferService.Generate(world, ctx, t));
            return all;
        }

        [Fact]
        public void BusinessClosed_GeneratesNothing()
        {
            var world = new WorldState { WorldSeed = 1 };

            Assert.Empty(RunTo(world, Context(), 10 * D));
            Assert.Empty(world.Offers);
        }

        [Fact]
        public void FirstOffersArriveOneDayAfterOpening()
        {
            var world = OpenWorld();

            Assert.Empty(RunTo(world, Context(), D - CoreTime.CheckIntervalTicks));
            var first = OfferService.Generate(world, Context(), D);

            Assert.Equal(2, first.Count);
            Assert.All(first, t => Assert.Equal(D, t.OfferedTick));
            Assert.All(first, t => Assert.Equal(D + Limits.OfferLifetime, t.OfferExpiresTick));
        }

        [Fact]
        public void LaterBatchesArriveEveryOneToThreeDays()
        {
            var world = OpenWorld();
            Assert.Equal(D, world.NextOfferTick);
            int batches = 0;
            for (long t = 0; t <= 30 * D; t += CoreTime.CheckIntervalTicks)
            {
                long due = world.NextOfferTick!.Value;
                int batch = world.NextOfferBatch;
                var created = OfferService.Generate(world, Context(), t);
                if (world.NextOfferBatch == batch)
                {
                    Assert.Empty(created);
                    continue;
                }
                batches++;
                Assert.Equal(batch + 1, world.NextOfferBatch);
                Assert.InRange(t, due, due + CoreTime.CheckIntervalTicks - 1);
                Assert.Equal(due + OfferService.OfferInterval(world.WorldSeed, batch), world.NextOfferTick);
                Assert.All(created, x => Assert.StartsWith("RWC-" + batch + "-", x.ContractId));
            }

            Assert.InRange(batches, 10, 30);
        }

        [Fact]
        public void OfferInterval_IsWithinOneToThreeDays_ReproducibleAndVaried()
        {
            var gaps = Enumerable.Range(0, 200).Select(i => OfferService.OfferInterval(2024, i)).ToList();

            Assert.All(gaps, g => Assert.InRange(g, D, 3 * D));
            Assert.Equal(gaps, Enumerable.Range(0, 200).Select(i => OfferService.OfferInterval(2024, i)));
            Assert.Contains(gaps, g => g < 1.5m * D);
            Assert.Contains(gaps, g => g > 2.5m * D);
        }

        [Fact]
        public void BothKindsPossible_OneOfEach()
        {
            var world = OpenWorld();

            var first = RunTo(world, Context(), D);

            Assert.Single(first, t => t.Kind == ContractKind.Normal);
            Assert.Single(first, t => t.Kind == ContractKind.HighRisk);
        }

        [Fact]
        public void NoPursuerCandidate_NoHighRiskAndNormalHasNoRaids()
        {
            var world = OpenWorld();

            var offers = RunTo(world, Context(withPursuer: false), 9 * D);

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.Equal(ContractKind.Normal, t.Kind));
            Assert.All(offers, t => Assert.Equal(0, t.ScheduledRaidCount));
            Assert.All(offers, t => Assert.Null(t.PursuerFactionId));
        }

        [Fact]
        public void OpenOffersNeverExceedFour_AndExpireAfterThreeDays()
        {
            var world = OpenWorld();
            int max = 0;
            for (long t = 0; t <= 20 * D; t += CoreTime.CheckIntervalTicks)
            {
                OfferService.Generate(world, Context(), t);
                max = System.Math.Max(max, world.Offers.Count);
                Assert.All(world.Offers, o => Assert.True(o.Terms.OfferExpiresTick > t));
            }

            Assert.Equal(Limits.MaxOpenOffers, max);
        }

        [Fact]
        public void ExpiredOffers_RemoveTheirCargoRecords()
        {
            var world = OpenWorld();
            var first = RunTo(world, Context(), D);
            var ids = first.SelectMany(t => t.CargoIds).ToList();
            Assert.All(ids, id => Assert.True(world.Cargo.ContainsKey(id)));

            RunTo(world, Context(), D + Limits.OfferLifetime);

            Assert.All(ids, id => Assert.False(world.Cargo.ContainsKey(id)));
        }

        [Fact]
        public void OfferedTerms_RespectValueRangesCrateCountAndMass()
        {
            var world = OpenWorld();

            var offers = RunTo(world, Context(), 30 * D);

            Assert.NotEmpty(offers);
            foreach (var t in offers)
            {
                var p = ContractParameters.For(t.Kind);
                Assert.InRange(t.TotalValue, p.MinTotalValue, p.MaxTotalValue);
                Assert.InRange(t.CargoIds.Count, Limits.MinCrates, Limits.MaxCrates);
                Assert.Equal(p.RequiredStorage, t.RequiredStorage);
                Assert.InRange(t.DurationTicks, 3 * D, 12 * D);
                Assert.Equal(0, t.DurationTicks % D);
                if (t.Kind == ContractKind.HighRisk) Assert.Equal(2, t.ScheduledRaidCount);
                else Assert.InRange(t.ScheduledRaidCount, 0, 1);
                Assert.NotEqual(t.DepositorFactionId, t.PursuerFactionId);
                Assert.Contains(t.DepositorFactionId, new[] { "Faction_1", "Faction_3" });
            }
            foreach (var c in world.Cargo.Values)
            {
                Assert.True(c.MassKg <= Limits.MaxCrateMassKg);
                Assert.Null(CargoService.Validate(c));
            }
            Assert.True(offers.Select(t => t.DurationTicks).Distinct().Count() > 1);
        }

        [Fact]
        public void ReopeningBusiness_DoesNotRerollSchedule()
        {
            var steady = OpenWorld();
            var toggled = OpenWorld();
            var a = new List<ContractTerms>();
            var b = new List<ContractTerms>();

            for (long t = 0; t <= 8 * D; t += CoreTime.CheckIntervalTicks)
            {
                if (t == D + 3000) OfferService.SetBusinessOpen(toggled, false, t);
                if (t == D + 9000) OfferService.SetBusinessOpen(toggled, true, t);
                a.AddRange(OfferService.Generate(steady, Context(), t));
                b.AddRange(OfferService.Generate(toggled, Context(), t));
            }

            Assert.Equal(a.Select(x => (x.ContractId, x.Seed, x.TotalValue, x.OfferedTick)), b.Select(x => (x.ContractId, x.Seed, x.TotalValue, x.OfferedTick)));
        }

        [Fact]
        public void BatchMissedWhileClosed_IsSkippedAndLaterBatchesAreUnchanged()
        {
            var steady = OpenWorld();
            var closed = OpenWorld();
            var a = new List<ContractTerms>();
            var b = new List<ContractTerms>();

            long second = D + OfferService.OfferInterval(steady.WorldSeed, 0);
            // Batch 1 is made on the first check at or after it is due; reopen on the check after that.
            long reopen = ((second + CoreTime.CheckIntervalTicks - 1) / CoreTime.CheckIntervalTicks + 1) * CoreTime.CheckIntervalTicks;

            for (long t = 0; t <= 12 * D; t += CoreTime.CheckIntervalTicks)
            {
                if (t == D + CoreTime.CheckIntervalTicks) OfferService.SetBusinessOpen(closed, false, t);
                if (t == reopen) OfferService.SetBusinessOpen(closed, true, t);
                a.AddRange(OfferService.Generate(steady, Context(), t));
                b.AddRange(OfferService.Generate(closed, Context(), t));
            }

            Assert.Contains(a, x => x.ContractId.StartsWith("RWC-1-"));
            Assert.DoesNotContain(b, x => x.ContractId.StartsWith("RWC-1-"));
            Assert.Equal(steady.NextOfferTick, closed.NextOfferTick);
            Assert.Equal(steady.NextOfferBatch, closed.NextOfferBatch);
            // The open-offer cap can differ while the skipped batch's offers would still be open, so compare the offers
            // both runs made: the same ID must mean the same draw.
            var later = b.Where(x => x.OfferedTick > second).ToDictionary(x => x.ContractId);
            var shared = a.Where(x => later.ContainsKey(x.ContractId)).ToList();
            Assert.NotEmpty(shared);
            Assert.All(shared, x => Assert.Equal((x.Seed, x.TotalValue, x.OfferedTick, x.DurationTicks), (later[x.ContractId].Seed, later[x.ContractId].TotalValue, later[x.ContractId].OfferedTick, later[x.ContractId].DurationTicks)));
        }

        [Fact]
        public void BlockedOrHostileFaction_IsNotADepositor()
        {
            var world = OpenWorld();
            world.FactionBlockedUntil["Faction_1"] = 100 * D;

            var offers = RunTo(world, Context(), 10 * D);

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.Equal("Faction_3", t.DepositorFactionId));
        }

        [Fact]
        public void DisabledKindInSettings_IsNotOffered()
        {
            var world = OpenWorld();
            world.Settings.HighRiskContractsEnabled = false;

            var offers = RunTo(world, Context(), 10 * D);

            Assert.NotEmpty(offers);
            Assert.All(offers, t => Assert.Equal(ContractKind.Normal, t.Kind));
        }

        [Fact]
        public void GeneratedOffer_CanBeAccepted()
        {
            var world = OpenWorld();
            var offer = RunTo(world, Context(), D).First(t => t.Kind == ContractKind.Normal);

            var d = ContractEngine.Accept(offer, Storage(4, 4), world, D + 10);

            Assert.True(d.Success, d.ReasonKey);
        }
    }
}
