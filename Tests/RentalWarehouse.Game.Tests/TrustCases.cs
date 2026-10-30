using System;
using System.Collections.Generic;
using LudeonTK;
using RentalWarehouse.Core;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>A17 parts that need the game: the ApplyTrust effect through the dispatcher and the rank-change message.</summary>
    public static class TrustCases
    {
        [DebugAction("Rental Warehouse tests", "Trust cases (A17)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Trust", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("ApplyTrustEffectUpdatesPointsOnce", ApplyTrustEffectUpdatesPointsOnce);
            yield return ("RankChangeIsAnnouncedOnce", RankChangeIsAnnouncedOnce);
            yield return ("ModSettingsFallBackToTheDefaultTableWhenAColumnIsInvalid", ModSettingsFallBack);
        }

        private static void ModSettingsFallBack(GameTestScope s)
        {
            var settings = new RentalWarehouseSettings();
            Check(TrustRanks.SameTable(TrustRanks.Default(), settings.RankRows()), "a new settings object holds the default table");

            settings.rankPoints[3] = -1;
            Check(TrustRanks.SameTable(TrustRanks.Default(), settings.RankRows()), "an invalid column falls back to the default");

            settings.rankCrates.RemoveAt(0);
            Check(TrustRanks.SameTable(TrustRanks.Default(), settings.RankRows()), "a short column falls back to the default");

            var edited = TrustRanks.Default();
            edited[2].NormalMax = 4321m;
            settings.SetRankRows(edited);
            Check(TrustRanks.SameTable(edited, settings.RankRows()), "a valid edited table is kept");
        }

        private static void ApplyTrustEffectUpdatesPointsOnce(GameTestScope s)
        {
            var state = s.World.State;
            int points = state.TrustPoints;
            state.TrustPoints = 0;
            try
            {
                var effect = new EffectRequest
                {
                    EffectId = "gt:trust:" + Rand.Int.ToString("X8"),
                    Kind = EffectKind.ApplyTrust,
                    EntityId = "GT",
                    Amount = 12m,
                    Payload = { ["empire"] = "0" },
                };
                s.World.Enqueue(new[] { effect });
                s.World.Enqueue(new[] { effect.Clone() });

                Equal(12, state.TrustPoints, "points after the same effect was queued twice");
            }
            finally
            {
                state.TrustPoints = points;
            }
        }

        private static void RankChangeIsAnnouncedOnce(GameTestScope s)
        {
            var state = s.World.State;
            int points = state.TrustPoints;
            int announced = state.TrustAnnouncedRank;
            try
            {
                state.TrustPoints = state.Settings.RankTable[1].RequiredPoints;
                state.TrustAnnouncedRank = 0;

                TrustAdapter.Tick(s.World);

                Equal(1, state.TrustAnnouncedRank, "announced rank after the first tick");
                Check(TrustService.TakeRankChange(state) == null, "nothing left to announce after the first tick");
            }
            finally
            {
                state.TrustPoints = points;
                state.TrustAnnouncedRank = announced;
            }
        }
    }
}
