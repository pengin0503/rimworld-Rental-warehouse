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
    /// Returning false means "not possible yet, retry later"; failed economic transfers retain their saved remainder.
    /// </summary>
    public static class EffectDispatcher
    {
        public delegate bool Handler(WarehouseWorldComponent world, EffectRequest effect);

        private static readonly Dictionary<EffectKind, Handler> Handlers = new Dictionary<EffectKind, Handler>
        {
            [EffectKind.ConsumeCargo] = ConsumeCargo,
            [EffectKind.RemoveCargo] = RemoveCargo,
            [EffectKind.ApplyGoodwill] = ApplyGoodwill,
            [EffectKind.ApplyTrust] = ApplyTrust,
            [EffectKind.Notify] = Notify,
            [EffectKind.DeliverCargo] = DeliveryAdapter.Deliver,
            [EffectKind.SpawnSilver] = PaymentAdapter.SpawnSilver,
            [EffectKind.ConsumeSilver] = PaymentAdapter.ConsumeSilver,
            [EffectKind.CreateReturnPod] = ReturnAdapter.CreatePod,
            [EffectKind.CreateSite] = VaultDiscovery.CreateSite,
            [EffectKind.CloseSite] = VaultDiscovery.CloseSite,
            [EffectKind.ReleaseVaultCargo] = VaultLootAdapter.ReleaseVaultCargo,
            [EffectKind.SpawnReinforcement] = VaultSiteHooks.SpawnReinforcement,
        };

        /// <summary>Later tasks register their handlers here (delivery, silver, raids, sites).</summary>
        public static void Register(EffectKind kind, Handler handler) => Handlers[kind] = handler;

        public static bool Apply(WarehouseWorldComponent world, EffectRequest effect)
        {
            if (world.State.Effects.IsDone(effect.EffectId)) return true;
            if (!Handlers.TryGetValue(effect.Kind, out var handler))
            {
                throw new InvalidOperationException("no handler for effect kind " + effect.Kind);
            }
            try { return handler(world, effect); }
            catch (Exception e)
            {
                Log.ErrorOnce("[RentalWarehouse] Effect " + effect.EffectId + " failed: " + e, effect.EffectId.GetHashCode());
                throw;
            }
        }

        public static void ProcessQueue(WarehouseWorldComponent world)
        {
            EffectQueue.Process(world.State, e => Apply(world, e), id => Diagnostics.Report("RW_Diag_EffectAbandoned", id), Ids.Now);
        }

        private static bool ConsumeCargo(WarehouseWorldComponent world, EffectRequest effect)
        {
            var batch = world.Batch(effect.EffectId);
            world.State.Cargo.TryGetValue(effect.EntityId, out var record);
            var crate = world.FindCrate(effect.EntityId);
            if (!batch.Prepared)
            {
                if (crate == null || record == null) return true;
                if (!crate.Spawned) return false;
                batch.MapId = Ids.Of(crate.Map);
                batch.Cell = crate.Position;
                batch.Prepare(() =>
                {
                    var contents = CargoAdapter.MakeContents(record, out var missing);
                    if (missing.Count > 0) Diagnostics.Report("RW_Diag_MissingDefs", record.CargoId + ": " + string.Join(", ", missing));
                    return contents;
                });
            }
            var map = Ids.Map(batch.MapId) ?? Find.AnyPlayerHomeMap;
            if (map == null) return false;
            var cell = batch.Cell.InBounds(map) ? batch.Cell : map.Center;
            if (!batch.Transfer(thing => GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near))) return false;
            if (crate != null) CargoAdapter.RemoveCrate(crate);
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

        private static bool ApplyTrust(WarehouseWorldComponent world, EffectRequest effect)
        {
            effect.Payload.TryGetValue("empire", out var empire);
            TrustService.Apply(world.State, (int)effect.Amount, empire == "1", effect.EntityId, Ids.Now);
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
