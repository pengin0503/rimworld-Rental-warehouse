using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using Verse.AI.Group;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>A04/A05 game parts: raid spawning, vault protection, crates carried off the map.</summary>
    public static class RaidCases
    {
        [DebugAction("Rental Warehouse tests", "Raid cases (A04/A05)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Raid", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("CrateInIntactVaultIsNotStealable", CrateInVaultNotStealable);
            yield return ("EmptyVaultIsNotABreachTarget", EmptyVaultNotTarget);
            yield return ("RaidSpawnsThievesWithCargoLord", RaidSpawns);
            yield return ("CrateCarriedOffMapIsLost_RecoveredCrateIsNot", CarriedOffMap);
        }

        private static Faction Hostile() =>
            Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && !f.defeated && f.def.humanlikeFaction && f.HostileTo(Faction.OfPlayer))
            ?? throw new GameTestFailure("no hostile humanlike faction in this world");

        private static void CrateInVaultNotStealable(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var vault = (Building_Vault)s.SpawnBuilding(RWDefOf.RW_Vault, room.Min);
            var crate = s.SpawnCrate(vault.Position, "Faction_GT", CargoCustody.Depositor, 3000m, StorageKind.Vault, "GT-K");
            Check(!ThreatAdapter.IsStealable(crate, s.Map), "crate inside an intact vault");
            Check(ThreatAdapter.HoldsContractCargo(vault), "vault with a crate is a breach target");
            vault.Destroy(DestroyMode.KillFinalize);
            Check(ThreatAdapter.IsStealable(crate, s.Map), "crate becomes stealable once the vault is gone");
        }

        private static void EmptyVaultNotTarget(GameTestScope s)
        {
            var room = s.BuildRoom(3);
            var vault = (Building_Vault)s.SpawnBuilding(RWDefOf.RW_Vault, room.Min);
            Check(!ThreatAdapter.HoldsContractCargo(vault), "empty vault is ignored");
        }

        private static void RaidSpawns(GameTestScope s)
        {
            var faction = Hostile();
            var terms = DeliveryCases.Offer(s, ContractKind.HighRisk, 3000m);
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            terms.PursuerFactionId = Ids.Of(faction);
            var c = new ContractState { Terms = terms, Phase = ContractPhase.Active, MapId = Ids.Of(s.Map), ActiveTick = Ids.Now, EndTick = Ids.Now + 6 * CoreTime.TicksPerDay };
            s.World.State.Contracts.Add(c);
            int before = s.Map.lordManager.lords.Count;

            bool started = ThreatAdapter.Start(s.World, new RaidDecision { ContractId = terms.ContractId, Points = 800m, SlotIndex = 0, EffectId = "gt" });

            Check(started, "raid started");
            var lord = s.Map.lordManager.lords.Skip(before).FirstOrDefault(l => l.LordJob is LordJob_StealCargo);
            Check(lord != null && lord.ownedPawns.Count > 0, "lord with pawns");
            foreach (var p in lord.ownedPawns.ToList()) p.Destroy(DestroyMode.Vanish);
        }

        private static void CarriedOffMap(GameTestScope s)
        {
            var faction = Hostile();
            var terms = DeliveryCases.Offer(s, ContractKind.Normal, 500m, 500m);
            s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            var c = new ContractState { Terms = terms, Phase = ContractPhase.Active, MapId = Ids.Of(s.Map), ActiveTick = Ids.Now, EndTick = Ids.Now + 6 * CoreTime.TicksPerDay, LastAccrualTick = Ids.Now };
            foreach (var id in terms.CargoIds) c.CargoStates.Add(new ContractCargoState { CargoId = id, DeclaredValue = 500m, Delivered = true });
            s.World.State.Contracts.Add(c);
            var area = s.FindClearRect(3);
            var crates = terms.CargoIds.Select(id => s.Spawn(CargoAdapter.MakeCrate(s.World.State.Cargo[id]), area.CenterCell)).ToList();
            var thief = PawnGenerator.GeneratePawn(PawnKindDefOf.Villager, faction);
            s.Spawn(thief, area.Min);

            crates[0].DeSpawn();
            thief.carryTracker.TryStartCarry(crates[0]);
            ThreatAdapter.TrackCarriedCrates(s.World, Ids.Now);
            Equal(LossCause.None, c.Cargo(terms.CargoIds[0]).Loss, "still on the map: not lost yet");

            thief.carryTracker.TryDropCarriedThing(thief.Position, ThingPlaceMode.Near, out _);
            ThreatAdapter.TrackCarriedCrates(s.World, Ids.Now);
            Equal(LossCause.None, c.Cargo(terms.CargoIds[0]).Loss, "recovered crate stays in the contract");

            var crate1 = crates[1];
            crate1.DeSpawn();
            thief.carryTracker.TryStartCarry(crate1);
            thief.DeSpawn();
            ThreatAdapter.TrackCarriedCrates(s.World, Ids.Now);
            Equal(LossCause.Lost, c.Cargo(terms.CargoIds[1]).Loss, "crate carried off the map is lost");
            if (!thief.Destroyed) thief.Destroy();
        }
    }
}
