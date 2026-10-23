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

            for (long t = 0; t <= 6 * D; t += CoreTime.CheckIntervalTicks)
            {
                if (t == 2 * D) OfferService.SetBusinessOpen(closed, false, t);
                if (t == 4 * D) OfferService.SetBusinessOpen(closed, true, t);
                a.AddRange(OfferService.Generate(steady, Context(), t));
                b.AddRange(OfferService.Generate(closed, Context(), t));
            }

            Assert.DoesNotContain(b, x => x.OfferedTick == 3 * D);
            var laterA = a.Where(x => x.OfferedTick == 5 * D).Select(x => (x.ContractId, x.Seed, x.TotalValue));
            var laterB = b.Where(x => x.OfferedTick == 5 * D).Select(x => (x.ContractId, x.Seed, x.TotalValue));
            Assert.Equal(laterA, laterB);
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
