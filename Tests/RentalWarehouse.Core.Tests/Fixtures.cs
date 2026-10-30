using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;

namespace RentalWarehouse.Core.Tests
{
    internal static class Fixtures
    {
        public const long D = CoreTime.TicksPerDay;
        /// <summary>Term used by fixtures; real offers draw 3–12 days.</summary>
        public const long Term = 6 * D;
        public const string MapId = "Map_0";
        public const string Depositor = "Faction_1";
        public const string Pursuer = "Faction_2";

        public static ContractTerms Terms(ContractKind kind, string contractId, decimal[] values, int raids = 0, long now = 0, long term = Term, decimal prepay = 0m)
        {
            var cargo = values.Select((v, i) => (contractId + "-C" + i, v)).ToList();
            return ContractTermsFactory.Create(
                ContractParameters.For(kind), contractId, Depositor, Pursuer, cargo, raids, term, prepay,
                new WarehouseSettings(), settingsVersion: 0, seed: 12345, now: now);
        }

        public static WorldState World(params ContractTerms[] offers)
        {
            var world = new WorldState { BusinessOpen = true };
            foreach (var t in offers) AddOffer(world, t);
            return world;
        }

        public static void AddOffer(WorldState world, ContractTerms terms)
        {
            world.Offers.Add(new ContractState { Terms = terms, Phase = ContractPhase.Offered });
            for (int i = 0; i < terms.CargoIds.Count; i++)
            {
                world.Cargo[terms.CargoIds[i]] = new CargoRecord
                {
                    CargoId = terms.CargoIds[i],
                    ContractId = terms.ContractId,
                    OwnerFactionId = terms.DepositorFactionId,
                    Custody = CargoCustody.Depositor,
                    DeclaredValue = terms.CargoValues[i],
                    MassKg = 10m,
                    RequiredStorage = terms.RequiredStorage,
                };
            }
        }

        public static StorageSnapshot Storage(int rackFree = 4, int vaultFree = 0, params string[] stored)
        {
            var s = new StorageSnapshot { MapId = MapId };
            if (rackFree > 0) s.Facilities.Add(new FacilitySnapshot { FacilityId = "Rack_1", Kind = StorageKind.Rack, FreeSlots = rackFree });
            if (vaultFree > 0) s.Facilities.Add(new FacilitySnapshot { FacilityId = "Vault_1", Kind = StorageKind.Vault, FreeSlots = vaultFree });
            foreach (var id in stored) s.ValidStoredCargoIds.Add(id);
            return s;
        }

        public static StorageSnapshot StoredAll(ContractState c) => Storage(0, 0, c.Terms.CargoIds.ToArray());

        public static StorageSnapshot StoredExcept(ContractState c, params string[] missing) =>
            Storage(0, 0, c.Terms.CargoIds.Where(id => !missing.Contains(id)).ToArray());

        /// <summary>Accepts at tick 0, delivers, stores and activates at tick 2D.</summary>
        public static ContractState Activate(WorldState world, ContractTerms terms, StorageSnapshot? capacity = null) =>
            ActivateWithEffects(world, terms, out _, capacity);

        /// <summary>Like <see cref="Activate"/>, also returning what the step that starts the term emitted.</summary>
        public static ContractState ActivateWithEffects(WorldState world, ContractTerms terms, out List<EffectRequest> startEffects, StorageSnapshot? capacity = null)
        {
            var accepted = ContractEngine.Accept(terms, capacity ?? Storage(4, 4), world, 0);
            Xunit.Assert.True(accepted.Success, accepted.ReasonKey);
            var c = world.Contract(terms.ContractId)!;
            c.MapId = MapId;
            ContractEngine.Advance(c, Storage(4, 4), D);
            foreach (var id in terms.CargoIds) ContractEngine.MarkDelivered(c, id, D);
            var active = ContractEngine.Advance(c, StoredAll(c), 2 * D);
            Xunit.Assert.Equal(ContractPhase.Active, active.NewState.Phase);
            startEffects = active.Effects;
            return c;
        }

        /// <summary>Runs the 250-tick check loop from the contract's last accrual to <paramref name="until"/>.</summary>
        public static List<EffectRequest> RunUntil(ContractState c, long until, System.Func<long, StorageSnapshot>? storageAt = null)
        {
            var effects = new List<EffectRequest>();
            for (long t = c.LastAccrualTick + CoreTime.CheckIntervalTicks; t <= until; t += CoreTime.CheckIntervalTicks)
            {
                var d = ContractEngine.Advance(c, storageAt?.Invoke(t) ?? StoredAll(c), t);
                effects.AddRange(d.Effects);
            }
            return effects;
        }

        public static void ReturnAll(ContractState c, long now)
        {
            foreach (var cs in c.CargoStates.Where(x => x.Loss == LossCause.None && !x.Returned))
            {
                ContractEngine.MarkReturned(c, cs.CargoId, now);
            }
        }
    }
}
