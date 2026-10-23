using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RentalWarehouse
{
    /// <summary>
    /// Game side of dedicated cargo raids (spec 6): raid context for the Core scheduler, raid spawning, and tracking
    /// of crates carried toward or across the map edge. Crates are followed by ID through the warehouse registry,
    /// never by scanning every Thing.
    /// </summary>
    public static class ThreatAdapter
    {
        private static readonly HashSet<string> carriedAlerted = new HashSet<string>();

        public static void Tick(WarehouseWorldComponent world, long now)
        {
            var state = world.State;
            foreach (var map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                if (GenHostility.AnyHostileActiveThreatToPlayer(map)) state.LastHostileTick[Ids.Of(map)] = now;
            }
            ThreatScheduler.Tick(state, c => ContextFor(world, c, now), d => Start(world, d), now);
            TrackCarriedCrates(world, now);
        }

        public static RaidContext Context(WarehouseWorldComponent world, string mapId, long now)
        {
            var map = Ids.Map(mapId);
            var ctx = new RaidContext { NowTick = now };
            if (map == null) return ctx;
            ctx.HostilePresent = GenHostility.AnyHostileActiveThreatToPlayer(map);
            if (world.State.LastHostileTick.TryGetValue(mapId, out long last) && !ctx.HostilePresent) ctx.LastOrdinaryRaidEndTick = last;
            return ctx;
        }

        /// <summary>Fills the attacker-specific part of the context for one contract.</summary>
        private static void Complete(RaidContext ctx, ContractState contract, Map map)
        {
            var faction = Ids.Faction(contract.Terms.PursuerFactionId);
            ctx.BaselinePoints = contract.BaselineThreatPoints > 0m ? contract.BaselineThreatPoints : (decimal)StorytellerUtility.DefaultThreatPointsNow(map);
            // A faction without any combat group for these points can never raid: skip instead of retrying for two days.
            float points = (float)(ctx.BaselinePoints * ContractParameters.For(contract.Terms.Kind).RaidPointFactor * contract.Terms.RaidScale);
            ctx.ValidAttacker = faction != null && !faction.defeated && faction.def.humanlikeFaction && faction.HostileTo(Faction.OfPlayer)
                && PawnGroupMakerUtility.CanGenerateAnyNormalGroup(faction, points);
            ctx.MinimumGroupPoints = faction == null ? decimal.MaxValue : (decimal)faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat);
        }

        /// <summary>Spawns the raid. Returns false (raid not counted, retried later) if no pawn could be generated or placed.</summary>
        public static bool Start(WarehouseWorldComponent world, RaidDecision decision)
        {
            var contract = world.State.Contract(decision.ContractId);
            var map = contract == null ? null : Ids.Map(contract.MapId);
            var faction = contract == null ? null : Ids.Faction(contract.Terms.PursuerFactionId);
            if (map == null || faction == null || faction.defeated) return false;
            var parms = new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                tile = map.Tile,
                faction = faction,
                points = (float)decision.Points,
                generateFightersOnly = true,
            };
            if (parms.points < faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)) return false;
            if (!RCellFinder.TryFindRandomPawnEntryCell(out var entry, map, CellFinder.EdgeRoadChance_Hostile)) return false;
            var pawns = PawnGroupMakerUtility.GeneratePawns(parms, warnOnZeroResults: false).ToList();
            if (pawns.Count == 0) return false;
            foreach (var p in pawns) GenSpawn.Spawn(p, CellFinder.RandomClosewalkCellNear(entry, map, 8), map);
            LordMaker.MakeNewLord(faction, new LordJob_StealCargo(contract.Terms.ContractId), map, pawns);
            Find.LetterStack.ReceiveLetter("RW_Letter_CargoRaidTitle".Translate(), "RW_Letter_CargoRaid".Translate(faction.Name, contract.Terms.ContractId, pawns.Count),
                LetterDefOf.ThreatBig, new LookTargets(pawns));
            return true;
        }

        public static RaidContext ContextFor(WarehouseWorldComponent world, ContractState contract, long now)
        {
            var ctx = Context(world, contract.MapId, now);
            var map = Ids.Map(contract.MapId);
            if (map != null) Complete(ctx, contract, map);
            return ctx;
        }

        /// <summary>A crate on this map that raiders may pick up: a contract crate not inside an intact vault.</summary>
        public static bool IsStealable(Thing crate, Map map)
        {
            if (crate == null || crate.Destroyed || !crate.Spawned || crate.Map != map) return false;
            var record = CargoAdapter.Comp(crate)?.Record;
            if (record?.ContractId == null || record.Custody != CargoCustody.Depositor) return false;
            return !(crate.Position.GetFirstThing<Building_Vault>(map) is Building_Vault vault) || vault.Destroyed;
        }

        public static bool HoldsContractCargo(Building_Vault vault)
        {
            foreach (var crate in vault.StoredCrates())
            {
                if (CargoAdapter.Comp(crate)?.Record?.ContractId != null) return true;
            }
            return false;
        }

        /// <summary>
        /// Spec 5/6: a crate carried across the map edge by raiders is lost; one taken back before that stays in the
        /// contract. Also warns once when a raider picks a crate up.
        /// </summary>
        public static void TrackCarriedCrates(WarehouseWorldComponent world, long now)
        {
            foreach (var kv in world.AllCrates.ToList())
            {
                var crate = kv.Value;
                if (crate.Destroyed || !world.State.Cargo.TryGetValue(kv.Key, out var record) || record.ContractId == null || record.Custody != CargoCustody.Depositor) continue;
                var carrier = Carrier(crate);
                bool hostileCarrier = carrier != null && carrier.Faction != null && carrier.Faction.HostileTo(Faction.OfPlayer);
                if (!hostileCarrier)
                {
                    carriedAlerted.Remove(kv.Key);
                    continue;
                }
                if (crate.MapHeld == null || !carrier.Spawned && carrier.MapHeld == null)
                {
                    record.Custody = CargoCustody.StolenByEnemy;
                    var contract = world.State.Contract(record.ContractId);
                    CargoAdapter.RemoveCrate(crate);
                    if (contract != null) world.Enqueue(ContractEngine.MarkLost(contract, kv.Key, LossCause.Lost, now).Effects);
                    PaymentService.SyncDebts(world.State, now);
                    carriedAlerted.Remove(kv.Key);
                }
                else if (carriedAlerted.Add(kv.Key))
                {
                    Messages.Message("RW_Msg_CrateCarried".Translate(record.ContractId), carrier, MessageTypeDefOf.ThreatBig, historical: true);
                }
            }
        }

        public static Pawn Carrier(Thing crate)
        {
            switch (crate.ParentHolder)
            {
                case Pawn_CarryTracker carry: return carry.pawn;
                case Pawn_InventoryTracker inv: return inv.pawn;
                default: return null;
            }
        }
    }
}
