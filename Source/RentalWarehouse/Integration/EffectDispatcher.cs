using System;
using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Applies Core <see cref="EffectRequest"/>s to the game. Every handler reconciles first: if the result already
    /// exists in the game (for example a crate with that cargo ID), it reports success without doing it again.
    /// Returning false means "not possible yet, retry later"; throwing counts toward abandonment.
    /// </summary>
    public static class EffectDispatcher
    {
        public delegate bool Handler(WarehouseWorldComponent world, EffectRequest effect);

        private static readonly Dictionary<EffectKind, Handler> Handlers = new Dictionary<EffectKind, Handler>
        {
            [EffectKind.ConsumeCargo] = ConsumeCargo,
            [EffectKind.RemoveCargo] = RemoveCargo,
            [EffectKind.ApplyGoodwill] = ApplyGoodwill,
            [EffectKind.Notify] = Notify,
            [EffectKind.DeliverCargo] = DeliveryAdapter.Deliver,
        };

        /// <summary>Later tasks register their handlers here (delivery, silver, raids, sites).</summary>
        public static void Register(EffectKind kind, Handler handler) => Handlers[kind] = handler;

        public static bool Apply(WarehouseWorldComponent world, EffectRequest effect)
        {
            if (!Handlers.TryGetValue(effect.Kind, out var handler))
            {
                throw new InvalidOperationException("no handler for effect kind " + effect.Kind);
            }
            return handler(world, effect);
        }

        public static void ProcessQueue(WarehouseWorldComponent world)
        {
            EffectQueue.Process(world.State, e => Apply(world, e), id => Diagnostics.Report("RW_Diag_EffectAbandoned", id), Ids.Now);
        }

        private static bool ConsumeCargo(WarehouseWorldComponent world, EffectRequest effect)
        {
            if (!world.State.Cargo.TryGetValue(effect.EntityId, out var record)) return true;
            var crate = world.FindCrate(effect.EntityId);
            if (crate == null) return true; // already consumed
            if (!crate.Spawned) return false; // opened only on a map; wait until it is placed
            var map = crate.Map;
            var pos = crate.Position;
            var contents = CargoAdapter.MakeContents(record, out var missing);
            foreach (var thing in contents) GenPlace.TryPlaceThing(thing, pos, map, ThingPlaceMode.Near);
            CargoAdapter.RemoveCrate(crate);
            if (missing.Count > 0) Diagnostics.Report("RW_Diag_MissingDefs", record.CargoId + ": " + string.Join(", ", missing));
            return true;
        }

        private static bool RemoveCargo(WarehouseWorldComponent world, EffectRequest effect)
        {
            var crate = world.FindCrate(effect.EntityId);
            if (crate != null) CargoAdapter.RemoveCrate(crate);
            if (world.State.Cargo.TryGetValue(effect.EntityId, out var record) && record.Custody == CargoCustody.Depositor)
            {
                record.Custody = CargoCustody.Returned;
            }
            return true;
        }

        private static bool ApplyGoodwill(WarehouseWorldComponent world, EffectRequest effect)
        {
            var faction = Ids.Faction(effect.EntityId);
            if (faction == null || faction.defeated || faction.IsPlayer) return true;
            faction.TryAffectGoodwillWith(Faction.OfPlayer, (int)effect.Amount, canSendMessage: true, canSendHostilityLetter: true);
            return true;
        }

        private static bool Notify(WarehouseWorldComponent world, EffectRequest effect)
        {
            if (!effect.Payload.TryGetValue("key", out var key)) return true;
            string contract = effect.EntityId;
            string amount = effect.Amount.ToString("0");
            var type = effect.Amount < 0m ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.NeutralEvent;
            Messages.Message(key.Translate(contract, amount), type, historical: true);
            return true;
        }
    }
}
