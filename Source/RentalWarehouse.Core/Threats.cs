using System;
using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    public enum RaidOutcome
    {
        None,
        Start,
        Defer,
        Skip,
    }

    public sealed class RaidContext
    {
        public long NowTick;
        public bool HostilePresent;
        public long? LastOrdinaryRaidEndTick;
        public long? LastDedicatedRaidStartTick;
        public bool ValidAttacker;
        public decimal BaselinePoints;
        public decimal MinimumGroupPoints;
    }

    public sealed class RaidDecision
    {
        public RaidOutcome Outcome;
        public long? NextTick;
        public decimal Points;
        public string EffectId = "";
        public int SlotIndex = -1;
        public string ContractId = "";
    }

    /// <summary>
    /// Dedicated cargo raids (spec 6). Planned from the contract seed at activation; started at most once per slot; deferred
    /// while hostiles are present, within half a day of a raid ending, or within a day of another dedicated raid on the
    /// same map; skipped after two days of deferral or at maturity. Only raids that actually started count.
    /// </summary>
    public static class ThreatScheduler
    {
        public const long MinGapBetweenDedicated = CoreTime.TicksPerDay;
        public const long QuietAfterRaid = CoreTime.TicksPerDay / 2;
        public const long MaxDeferral = 2 * CoreTime.TicksPerDay;
        /// <summary>
        /// Raid timing as thirds of the term (days 2 and 4 of a 6-day term), so a short term still fits every raid before
        /// maturity instead of skipping it and losing completion bonus.
        /// </summary>
        public const int NormalWindowStartThirds = 1;
        public const int NormalWindowEndThirds = 2;
        public static readonly int[] HighRiskThirds = { 1, 2 };

        public static void EnsurePlan(ContractState c)
        {
            if (c.RaidPlan.Count > 0 || c.ActiveTick == null || c.Terms.ScheduledRaidCount <= 0) return;
            long active = c.ActiveTick.Value;
            long third = c.Terms.DurationTicks / 3;
            var rng = new DeterministicRandom(DeterministicRandom.Mix(c.Terms.Seed, "raids"));
            if (c.Terms.Kind == ContractKind.HighRisk)
            {
                for (int i = 0; i < c.Terms.ScheduledRaidCount; i++)
                {
                    long offset = third * (i < HighRiskThirds.Length ? HighRiskThirds[i] : HighRiskThirds[HighRiskThirds.Length - 1]);
                    c.RaidPlan.Add(new RaidSlot { Index = i, PlannedTick = active + offset });
                }
            }
            else
            {
                long windowStart = third * NormalWindowStartThirds;
                long windowEnd = third * NormalWindowEndThirds;
                for (int i = 0; i < c.Terms.ScheduledRaidCount; i++)
                {
                    long offset = windowStart + (long)(rng.NextDecimal() * (windowEnd - windowStart));
                    c.RaidPlan.Add(new RaidSlot { Index = i, PlannedTick = active + offset });
                }
                c.RaidPlan.Sort((a, b) => a.PlannedTick.CompareTo(b.PlannedTick));
                for (int i = 0; i < c.RaidPlan.Count; i++) c.RaidPlan[i].Index = i;
            }
        }

        public static RaidDecision Evaluate(ContractState c, RaidContext ctx)
        {
            var none = new RaidDecision { Outcome = RaidOutcome.None, ContractId = c.Terms.ContractId };
            if (c.Terms.ScheduledRaidCount <= 0) return none;

            if (c.Phase != ContractPhase.Active)
            {
                // Spec 5: no new dedicated raid after maturity (or once the contract stopped being stored).
                if (c.Phase == ContractPhase.Returning || c.IsTerminal) SkipAllPending(c);
                return none;
            }
            EnsurePlan(c);
            var slot = c.RaidPlan.Find(r => r.State == RaidSlotState.Pending);
            if (slot == null || ctx.NowTick < slot.PlannedTick) return none;

            var decision = new RaidDecision { ContractId = c.Terms.ContractId, SlotIndex = slot.Index, EffectId = ContractEngine.Id(c, "raid", slot.Index.ToString(CultureInfo.InvariantCulture)) };
            long deadline = Math.Min(slot.PlannedTick + MaxDeferral, c.EndTick ?? long.MaxValue);
            if (ctx.NowTick >= deadline || !ctx.ValidAttacker)
            {
                slot.State = RaidSlotState.Skipped;
                decision.Outcome = RaidOutcome.Skip;
                return decision;
            }

            decimal factor = ContractParameters.For(c.Terms.Kind).RaidPointFactor;
            decimal baseline = c.BaselineThreatPoints > 0m ? c.BaselineThreatPoints : ctx.BaselinePoints;
            decision.Points = baseline * factor * c.Terms.RaidScale;
            if (decision.Points < ctx.MinimumGroupPoints)
            {
                slot.State = RaidSlotState.Skipped;
                decision.Outcome = RaidOutcome.Skip;
                return decision;
            }

            long? wait = null;
            if (ctx.HostilePresent) wait = ctx.NowTick + CoreTime.CheckIntervalTicks;
            if (ctx.LastOrdinaryRaidEndTick.HasValue && ctx.NowTick - ctx.LastOrdinaryRaidEndTick.Value < QuietAfterRaid)
            {
                wait = Max(wait, ctx.LastOrdinaryRaidEndTick.Value + QuietAfterRaid);
            }
            if (ctx.LastDedicatedRaidStartTick.HasValue && ctx.NowTick - ctx.LastDedicatedRaidStartTick.Value < MinGapBetweenDedicated)
            {
                wait = Max(wait, ctx.LastDedicatedRaidStartTick.Value + MinGapBetweenDedicated);
            }
            if (wait.HasValue)
            {
                decision.Outcome = RaidOutcome.Defer;
                decision.NextTick = wait;
                return decision;
            }
            decision.Outcome = RaidOutcome.Start;
            return decision;
        }

        private static long Max(long? a, long b) => a.HasValue ? Math.Max(a.Value, b) : b;

        private static void SkipAllPending(ContractState c)
        {
            foreach (var slot in c.RaidPlan)
            {
                if (slot.State == RaidSlotState.Pending) slot.State = RaidSlotState.Skipped;
            }
        }

        /// <summary>Called only after the game really spawned the raid (spec 6: failed generation is not a started raid).</summary>
        public static void MarkStarted(ContractState c, int slotIndex, long now)
        {
            var slot = c.RaidPlan.Find(r => r.Index == slotIndex);
            if (slot == null || slot.State != RaidSlotState.Pending) return;
            slot.State = RaidSlotState.Started;
            slot.StartedTick = now;
            c.StartedRaids++;
        }

        public static long? LastDedicatedStart(WorldState world, string mapId)
        {
            long? last = null;
            foreach (var c in world.Contracts)
            {
                if (c.MapId != mapId) continue;
                foreach (var slot in c.RaidPlan)
                {
                    if (slot.StartedTick.HasValue && (!last.HasValue || slot.StartedTick.Value > last.Value)) last = slot.StartedTick;
                }
            }
            return last;
        }

        /// <summary>
        /// Evaluates every contract that has something to decide, in contract-ID order, so simultaneous candidates on a map start
        /// one day apart. A contract whose next raid is not due yet is left alone and never costs a <paramref name="context"/>:
        /// reading the game for it (hostiles, threat points, raid groups) is the expensive part of a look.
        /// </summary>
        public static void Tick(WorldState world, Func<ContractState, RaidContext> context, Func<RaidDecision, bool> start, long now)
        {
            List<ContractState>? ordered = null;
            foreach (var c in world.Contracts)
            {
                if (NeedsLook(c, now)) (ordered ??= new List<ContractState>()).Add(c);
            }
            if (ordered == null) return;

            ordered.Sort((a, b) => string.CompareOrdinal(a.Terms.ContractId, b.Terms.ContractId));
            foreach (var c in ordered)
            {
                // Outside the active term the only decision is to give up the pending raids, which needs nothing from the game.
                var ctx = c.Phase == ContractPhase.Active ? context(c) : new RaidContext();
                ctx.NowTick = now;
                ctx.LastDedicatedRaidStartTick = LastDedicatedStart(world, c.MapId);
                var decision = Evaluate(c, ctx);
                if (decision.Outcome == RaidOutcome.Start && start(decision)) MarkStarted(c, decision.SlotIndex, now);
            }
        }

        /// <summary>Whether <see cref="Evaluate"/> can decide anything for this contract now: a raid is due, or pending raids must be dropped.</summary>
        private static bool NeedsLook(ContractState c, long now)
        {
            if (c.Terms.ScheduledRaidCount <= 0) return false;
            if (c.Phase != ContractPhase.Active)
            {
                return (c.Phase == ContractPhase.Returning || c.IsTerminal) && c.RaidPlan.Exists(r => r.State == RaidSlotState.Pending);
            }
            EnsurePlan(c);
            var slot = c.RaidPlan.Find(r => r.State == RaidSlotState.Pending);
            return slot != null && now >= slot.PlannedTick;
        }
    }
}
