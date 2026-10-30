using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    /// <summary>Spec 5: the Empire offers contracts from a Royalty-only catalogue once the rank and a Knight-or-higher colonist allow it.</summary>
    public class EmpireTests
    {
        private static readonly List<Good> EmpireGoods = new List<Good>
        {
            new Good("MeleeWeapon_MonoSword", null, 2000m, 2m),
            new Good("MeleeWeapon_PlasmaSwordBladelink", null, 3000m, 2m),
            new Good("Apparel_PsyfocusRobe", null, 600m, 0.5m),
            new Good("BroadshieldCore", null, 350m, 2m),
        };

        private static OfferContext EmpireContext(decimal factor = 1.0m, bool withGoods = true)
        {
            var ctx = OfferTests.Context();
            ctx.Factions.Add(new FactionInfo { FactionId = "Faction_E", Humanlike = true, IsEmpire = true });
            ctx.Factions.First(f => f.FactionId == "Faction_2").HostileTo.Add("Faction_E");
            ctx.EmpireTitleFactor = factor;
            if (withGoods) ctx.EmpireGoods = EmpireGoods;
            return ctx;
        }

        private static WorldState WorldWithPoints(int points)
        {
            var world = OfferTests.OpenWorld();
            world.TrustPoints = points;
            return world;
        }

        /// <summary>Generates for <paramref name="days"/> and records each new offer with the item names in its crates.</summary>
        private static List<(ContractTerms Terms, List<string> Items)> Generate(WorldState world, OfferContext ctx, long days)
        {
            var result = new List<(ContractTerms, List<string>)>();
            for (long t = 0; t <= days * D; t += CoreTime.CheckIntervalTicks)
            {
                foreach (var terms in OfferService.Generate(world, ctx, t))
                {
                    result.Add((terms, terms.CargoIds.SelectMany(id => world.Cargo[id].Manifest.Select(m => m.ThingDefName)).ToList()));
                }
            }
            return result;
        }

        [Fact]
        public void EmpireTitleFactor_StartsAtKnight()
        {
            Assert.Equal(0m, TrustRanks.EmpireTitleFactor(null, 300));
            Assert.Equal(0m, TrustRanks.EmpireTitleFactor(200, 300));
            Assert.Equal(1.0m, TrustRanks.EmpireTitleFactor(300, 300));
            Assert.Equal(1.1m, TrustRanks.EmpireTitleFactor(400, 300));
            Assert.Equal(1.3m, TrustRanks.EmpireTitleFactor(600, 300));
        }

        [Fact]
        public void EmpireTitleFactor_IsZeroWhenTheKnightTitleIsMissing()
        {
            Assert.Equal(0m, TrustRanks.EmpireTitleFactor(500, -1));
        }

        [Fact]
        public void Empire_NeverOffers_BelowRankTwo_ButDoesAtRankTwo()
        {
            var below = Generate(WorldWithPoints(99), EmpireContext(), 120);
            var atTwo = Generate(WorldWithPoints(100), EmpireContext(), 120);

            Assert.NotEmpty(below);
            Assert.DoesNotContain(below, o => o.Terms.DepositorIsEmpire);
            Assert.Contains(atTwo, o => o.Terms.DepositorIsEmpire);
        }

        [Fact]
        public void Empire_NeverOffers_WithoutATitleFactorOrACatalogue()
        {
            var noFactor = Generate(WorldWithPoints(1000), EmpireContext(factor: 0m), 120);
            var noGoods = Generate(WorldWithPoints(1000), EmpireContext(withGoods: false), 120);

            Assert.NotEmpty(noFactor);
            Assert.NotEmpty(noGoods);
            Assert.DoesNotContain(noFactor, o => o.Terms.DepositorIsEmpire);
            Assert.DoesNotContain(noGoods, o => o.Terms.DepositorIsEmpire);
        }

        [Fact]
        public void EmpireOffers_UseOnlyTheEmpireCatalogue_AndScaleWithTheFactor()
        {
            var world = WorldWithPoints(100);
            var row = TrustService.Row(world);
            var names = EmpireGoods.Select(g => g.DefName).ToHashSet();

            var offers = Generate(world, EmpireContext(factor: 1.3m), 150);

            var empire = offers.Where(o => o.Terms.DepositorIsEmpire).ToList();
            Assert.NotEmpty(empire);
            Assert.Contains(offers, o => !o.Terms.DepositorIsEmpire);
            foreach (var (terms, items) in empire)
            {
                bool danger = terms.Kind == ContractKind.HighRisk;
                Assert.Equal("Faction_E", terms.DepositorFactionId);
                Assert.InRange(terms.TotalValue, (danger ? row.HighRiskMin : row.NormalMin) * 1.3m, (danger ? row.HighRiskMax : row.NormalMax) * 1.3m);
                Assert.All(items, i => Assert.Contains(i, names));
            }
            foreach (var (terms, items) in offers.Where(o => !o.Terms.DepositorIsEmpire))
            {
                Assert.NotEqual("Faction_E", terms.DepositorFactionId);
                Assert.All(items, i => Assert.DoesNotContain(i, names));
            }
        }

        [Fact]
        public void EmpireOffersAlreadyMade_SurviveLosingEligibility()
        {
            var world = WorldWithPoints(100);
            var ctx = EmpireContext();
            ContractTerms? empire = null;
            long now = 0;
            for (; now <= 150 * D && empire == null; now += CoreTime.CheckIntervalTicks)
            {
                empire = OfferService.Generate(world, ctx, now).FirstOrDefault(t => t.DepositorIsEmpire);
            }
            Assert.NotNull(empire);

            ctx.EmpireTitleFactor = 0m;
            world.TrustPoints = 0;
            now += CoreTime.CheckIntervalTicks;
            OfferService.Generate(world, ctx, now);

            Assert.Contains(world.Offers, o => o.Terms.ContractId == empire!.ContractId);
            Assert.True(ContractEngine.Accept(empire!, Storage(12, 12), world, now).Success);
            Assert.DoesNotContain(Generate(world, ctx, 150), o => o.Terms.DepositorIsEmpire);
        }

        [Fact]
        public void DepositorIsEmpire_SurvivesPersistence()
        {
            var world = WorldWithPoints(100);
            var ctx = EmpireContext();
            ContractTerms? empire = null;
            for (long now = 0; now <= 150 * D && empire == null; now += CoreTime.CheckIntervalTicks)
            {
                empire = OfferService.Generate(world, ctx, now).FirstOrDefault(t => t.DepositorIsEmpire);
            }
            Assert.NotNull(empire);

            var loaded = StateCodec.Import(StateCodec.Export(world));

            Assert.True(loaded.Offers.Single(o => o.Terms.ContractId == empire!.ContractId).Terms.DepositorIsEmpire);
        }
    }
}
