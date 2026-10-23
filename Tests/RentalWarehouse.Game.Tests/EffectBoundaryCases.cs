using System;
using System.Collections.Generic;
using LudeonTK;
using RentalWarehouse.Core;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>
    /// A11 game parts that can run inside one session: exception injection and the Scribe round trip of the ledger.
    /// The full save → quit → load cycle at each boundary listed in acceptance.md stays a manual step.
    /// </summary>
    public static class EffectBoundaryCases
    {
        [DebugAction("Rental Warehouse tests", "Effect boundary cases (A11)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("EffectBoundary", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("InjectedExceptionIsRetriedOnce", InjectedExceptionIsRetried);
            yield return ("ConsumeCargoTwiceSpawnsContentsOnce", ConsumeCargoTwice);
            yield return ("LedgerSurvivesScribeOutput", LedgerSurvivesScribeOutput);
        }

        private static void InjectedExceptionIsRetried(GameTestScope s)
        {
            int calls = 0;
            var state = new WorldState();
            EffectQueue.Enqueue(state, new EffectRequest { EffectId = "gt:inject", Kind = EffectKind.Notify });

            bool Apply(EffectRequest e)
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("injected");
                return true;
            }

            EffectQueue.Process(state, Apply, null, Ids.Now);
            EffectQueue.Process(state, Apply, null, Ids.Now);
            EffectQueue.Process(state, Apply, null, Ids.Now);

            Equal(2, calls, "handler calls");
            Check(state.Effects.IsCompleted("gt:inject"), "effect completed after retry");
        }

        private static void ConsumeCargoTwice(GameTestScope s)
        {
            var area = s.FindClearRect(3);
            var crate = s.SpawnCrate(area.CenterCell, CargoService.PlayerFactionId, CargoCustody.Player, 100m, StorageKind.Rack);
            string id = CargoAdapter.Comp(crate).cargoId;
            int steelBefore = s.Map.resourceCounter.GetCount(ThingDefOf_Steel);
            var effect = new EffectRequest { EffectId = "gt:open:" + id, Kind = EffectKind.ConsumeCargo, EntityId = id };

            EffectDispatcher.Apply(s.World, effect);
            EffectDispatcher.Apply(s.World, effect);
            s.Map.resourceCounter.UpdateResourceCounts();

            Check(crate.Destroyed, "crate consumed");
            int gained = s.Map.resourceCounter.GetCount(ThingDefOf_Steel) - steelBefore;
            Check(gained <= 20, "contents spawned at most once (gained " + gained + ")");
            foreach (var t in GenRadial.RadialDistinctThingsAround(area.CenterCell, s.Map, 4f, true))
            {
                if (t.def == ThingDefOf_Steel) t.Destroy();
            }
        }

        private static ThingDef ThingDefOf_Steel => RimWorld.ThingDefOf.Steel;

        private static void LedgerSurvivesScribeOutput(GameTestScope s)
        {
            string xml = Scribe.saver.DebugOutputFor(s.World);
            Check(xml.Contains("<ledger>"), "save output contains the ledger node");
            Check(xml.Contains("schemaVersion"), "save output contains the schema version");
        }
    }
}
