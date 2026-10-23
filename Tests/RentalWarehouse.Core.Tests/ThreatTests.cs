using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;
using static RentalWarehouse.Core.Tests.Fixtures;

namespace RentalWarehouse.Core.Tests
{
    public class ThreatTests
    {
        private static RaidContext Calm(long now) => new RaidContext
        {
            NowTick = now,
            HostilePresent = false,
            ValidAttacker = true,
            BaselinePoints = 1000m,
            MinimumGroupPoints = 100m,
        };

        private static ContractState Active(ContractKind kind, int raids, string id = "K1", WorldState? world = null)
        {
            var terms = Terms(kind, id, new[] { kind == ContractKind.HighRisk ? 3000m : 1000m }, raids);
            world ??= World();
            AddOffer(world, terms);
            var c = Activate(world, terms, Storage(4, 4));
            c.BaselineThreatPoints = 1000m;
            return c;
        }

        [Fact]
        public void HighRisk_PlansTwoRaidsOnDaysTwoAndFour()
        {
            var c = Active(ContractKind.HighRisk, 2);

            ThreatScheduler.EnsurePlan(c);

            Assert.Equal(new[] { c.ActiveTick + 2 * D, c.ActiveTick + 4 * D }, c.RaidPlan.Select(r => (long?)r.PlannedTick));
        }

        [Fact]
        public void Normal_OneRaidPlannedBetweenDaysTwoAndFour_Reproducibly()
        {
            var a = Active(ContractKind.Normal, 1);
            var b = Active(ContractKind.Normal, 1);

            ThreatScheduler.EnsurePlan(a);
            ThreatScheduler.EnsurePlan(b);

            var slot = Assert.Single(a.RaidPlan);
            Assert.InRange(slot.PlannedTick, a.ActiveTick!.Value + 2 * D, a.ActiveTick!.Value + 4 * D);
            Assert.Equal(slot.PlannedTick - a.ActiveTick, b.RaidPlan[0].PlannedTick - b.ActiveTick);
        }

        [Fact]
        public void Normal_NoRaidScheduled_HasEmptyPlanAndNoDecision()
        {
            var c = Active(ContractKind.Normal, 0);

            var d = ThreatScheduler.Evaluate(c, Calm(c.ActiveTick!.Value + 3 * D));

            Assert.Empty(c.RaidPlan);
            Assert.Equal(RaidOutcome.None, d.Outcome);
        }

        [Fact]
        public void OfferRaidChance_IsAboutTwentyPercent()
        {
            int withRaid = 0, total = 0;
            for (int batch = 0; batch < 400; batch++)
            {
                var world = new WorldState { WorldSeed = batch };
                OfferService.SetBusinessOpen(world, true, 0);
                var ctx = new OfferContext
                {
                    Goods = new List<Good> { new Good("Silver", null, 1m, 0.008m), new Good("Gold", null, 10m, 0.008m) },
                    Factions =
                    {
                        new FactionInfo { FactionId = "Faction_1", Humanlike = true },
                        new FactionInfo { FactionId = "Faction_2", Humanlike = true, HostileToPlayer = true, HostileTo = { "Faction_1" } },
                    },
                };
                world.Settings.HighRiskContractsEnabled = false;
                foreach (var t in OfferService.Generate(world, ctx, D))
                {
                    total++;
                    if (t.ScheduledRaidCount == 1) withRaid++;
                }
            }
            Assert.InRange(withRaid / (double)total, 0.14, 0.26);
        }

        [Fact]
        public void Start_UsesFactorTimesBaselineTimesScale()
        {
            var c = Active(ContractKind.HighRisk, 2);
            c.Terms.RaidScale = 1.5m;

            var d = ThreatScheduler.Evaluate(c, Calm(c.ActiveTick!.Value + 2 * D));

            Assert.Equal(RaidOutcome.Start, d.Outcome);
            Assert.Equal(1500m, d.Points);
            Assert.Equal("K1:raid:0", d.EffectId);
            Assert.Equal(0, d.SlotIndex);
        }

        [Fact]
        public void NormalRaid_UsesSixtyPercent()
        {
            var c = Active(ContractKind.Normal, 1);
            ThreatScheduler.EnsurePlan(c);

            var d = ThreatScheduler.Evaluate(c, Calm(c.RaidPlan[0].PlannedTick));

            Assert.Equal(600m, d.Points);
        }

        [Fact]
        public void BeforePlannedTick_NothingHappens()
        {
            var c = Active(ContractKind.HighRisk, 2);

            Assert.Equal(RaidOutcome.None, ThreatScheduler.Evaluate(c, Calm(c.ActiveTick!.Value + 2 * D - 1)).Outcome);
        }

        [Fact]
        public void HostilesPresent_Defers()
        {
            var c = Active(ContractKind.HighRisk, 2);
            var ctx = Calm(c.ActiveTick!.Value + 2 * D);
            ctx.HostilePresent = true;

            Assert.Equal(RaidOutcome.Defer, ThreatScheduler.Evaluate(c, ctx).Outcome);
        }

        [Fact]
        public void WithinHalfDayOfOrdinaryRaid_Defers_ThenStarts()
        {
            var c = Active(ContractKind.HighRisk, 2);
            long t = c.ActiveTick!.Value + 2 * D;
            var ctx = Calm(t);
            ctx.LastOrdinaryRaidEndTick = t - D / 2 + 1;

            Assert.Equal(RaidOutcome.Defer, ThreatScheduler.Evaluate(c, ctx).Outcome);
            ctx.NowTick = t + 1;
            Assert.Equal(RaidOutcome.Start, ThreatScheduler.Evaluate(c, ctx).Outcome);
        }

        [Fact]
        public void DeferredTwoDays_IsSkippedAndNotCounted()
        {
            var c = Active(ContractKind.HighRisk, 2);
            long planned = c.ActiveTick!.Value + 2 * D;
            var ctx = Calm(planned + 2 * D);
            ctx.HostilePresent = true;

            var d = ThreatScheduler.Evaluate(c, ctx);

            Assert.Equal(RaidOutcome.Skip, d.Outcome);
            Assert.Equal(RaidSlotState.Skipped, c.RaidPlan[0].State);
            Assert.Equal(0, c.StartedRaids);
        }

        [Fact]
        public void SameMapDedicatedRaids_AreAtLeastOneDayApart_InContractIdOrder()
        {
            var world = World();
            var a = Active(ContractKind.HighRisk, 2, "K1", world);
            var b = Active(ContractKind.HighRisk, 2, "K2", world);
            long t = a.ActiveTick!.Value + 2 * D;
            var started = new List<string>();

            for (long now = t; now < t + 2 * D; now += CoreTime.CheckIntervalTicks)
            {
                ThreatScheduler.Tick(world, _ => Calm(now), d => { started.Add(d.EffectId + "@" + (now - t)); return true; }, now);
            }

            Assert.Equal("K1:raid:0@0", started[0]);
            Assert.Equal("K2:raid:0@" + D, started[1]);
            Assert.Equal(1, a.StartedRaids);
            Assert.Equal(1, b.StartedRaids);
        }

        [Fact]
        public void FailedStart_IsNotCounted_AndRetried()
        {
            var world = World();
            var c = Active(ContractKind.HighRisk, 2, "K1", world);
            long t = c.ActiveTick!.Value + 2 * D;
            int attempts = 0;

            ThreatScheduler.Tick(world, _ => Calm(t), d => { attempts++; return false; }, t);
            Assert.Equal(0, c.StartedRaids);
            ThreatScheduler.Tick(world, _ => Calm(t + 250), d => { attempts++; return true; }, t + 250);

            Assert.Equal(2, attempts);
            Assert.Equal(1, c.StartedRaids);
        }

        [Fact]
        public void NoValidAttackerOrTooFewPoints_Skips()
        {
            var c = Active(ContractKind.HighRisk, 2);
            var ctx = Calm(c.ActiveTick!.Value + 2 * D);
            ctx.ValidAttacker = false;
            Assert.Equal(RaidOutcome.Skip, ThreatScheduler.Evaluate(c, ctx).Outcome);

            var ctx2 = Calm(c.ActiveTick!.Value + 4 * D);
            ctx2.MinimumGroupPoints = 5000m;
            Assert.Equal(RaidOutcome.Skip, ThreatScheduler.Evaluate(c, ctx2).Outcome);
            Assert.All(c.RaidPlan, r => Assert.Equal(RaidSlotState.Skipped, r.State));
        }

        [Fact]
        public void AfterMaturity_NoNewRaids()
        {
            var world = World();
            var c = Active(ContractKind.HighRisk, 2, "K1", world);
            ThreatScheduler.EnsurePlan(c);
            RunUntil(c, c.EndTick!.Value);
            var started = new List<string>();

            ThreatScheduler.Tick(world, _ => Calm(c.EndTick!.Value + 1), d => { started.Add(d.EffectId); return true; }, c.EndTick!.Value + 1);

            Assert.Empty(started);
            Assert.All(c.RaidPlan, r => Assert.Equal(RaidSlotState.Skipped, r.State));
        }

        [Fact]
        public void SecondRaidDeferredPastMaturity_IsSkipped_FulfilmentHalves()
        {
            var world = World();
            var c = Active(ContractKind.HighRisk, 2, "K1", world);
            long active = c.ActiveTick!.Value;

            for (long now = active + 250; now <= c.EndTick!.Value; now += CoreTime.CheckIntervalTicks)
            {
                ContractEngine.Advance(c, StoredAll(c), now);
                bool hostile = now >= active + 4 * D;
                ThreatScheduler.Tick(world, _ => { var x = Calm(now); x.HostilePresent = hostile; return x; }, d => true, now);
            }
            ReturnAll(c, c.EndTick!.Value + 1);

            Assert.Equal(1, c.StartedRaids);
            Assert.Equal(RaidSlotState.Skipped, c.RaidPlan[1].State);
            // 3000 silver: 12 + 12 = 24/day -> 144 fees, completion 144 * 0.5 * 1 * 0.5 = 36
            Assert.Equal(36m, c.Settlement!.CompletionBonus);
        }

        [Fact]
        public void LastDedicatedStart_IsPerMap()
        {
            var world = World();
            var a = Active(ContractKind.HighRisk, 2, "K1", world);
            var b = Active(ContractKind.HighRisk, 2, "K2", world);
            b.MapId = "Map_7";
            ThreatScheduler.EnsurePlan(a);
            ThreatScheduler.MarkStarted(a, 0, 500);

            Assert.Equal(500, ThreatScheduler.LastDedicatedStart(world, MapId));
            Assert.Null(ThreatScheduler.LastDedicatedStart(world, "Map_7"));
        }
    }
}
