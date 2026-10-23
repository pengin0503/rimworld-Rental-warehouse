using System.Collections.Generic;
using System.Linq;
using RentalWarehouse.Core;
using Xunit;

namespace RentalWarehouse.Core.Tests
{
    public class CargoTests
    {
        private static CargoRecord Crate(CargoCustody custody, decimal value = 1200m, string id = "C1") => new CargoRecord
        {
            CargoId = id,
            OwnerFactionId = custody == CargoCustody.Player ? CargoService.PlayerFactionId : "Faction_1",
            Custody = custody,
            DeclaredValue = value,
            MassKg = 20m,
            Manifest = { new ManifestEntry { ThingDefName = "Steel", Count = 60 } },
        };

        [Fact]
        public void EntrustedCrate_HasNoWealthValue()
        {
            Assert.Equal(0m, CargoService.WealthValue(Crate(CargoCustody.Depositor)));
        }

        [Fact]
        public void StolenCrate_CountsDeclaredValue()
        {
            var stolen = Crate(CargoCustody.Depositor);
            var take = CargoService.TakeFromSite(stolen);

            Assert.True(take.Success);
            Assert.True(stolen.Stolen);
            Assert.Equal(CargoCustody.Player, stolen.Custody);
            Assert.Equal("Faction_1", stolen.OriginalOwnerFactionId);
            Assert.Equal(1200m, CargoService.WealthValue(stolen));
        }

        [Fact]
        public void OpenedCrate_MovesValueToContents()
        {
            var crate = Crate(CargoCustody.Player);
            CargoService.Open(crate, "open:C1");

            Assert.Equal(0m, CargoService.WealthValue(crate));
        }

        [Fact]
        public void Open_SecondTime_ProducesNoEffects()
        {
            var crate = Crate(CargoCustody.Player);

            var first = CargoService.Open(crate, "open:C1");
            var second = CargoService.Open(crate, "open:C1");

            var effect = Assert.Single(first.Effects);
            Assert.Equal(EffectKind.ConsumeCargo, effect.Kind);
            Assert.Equal("C1", effect.EntityId);
            Assert.False(second.Success);
            Assert.Empty(second.Effects);
        }

        [Fact]
        public void Open_DestroyedCrate_IsRejected()
        {
            var crate = Crate(CargoCustody.Destroyed);

            Assert.False(CargoService.Open(crate, "open:C1").Success);
        }

        [Fact]
        public void Validate_RejectsCrateOver35Kg()
        {
            var crate = Crate(CargoCustody.Depositor);
            crate.MassKg = 35.01m;

            Assert.Equal("RW_Reject_TooHeavy", CargoService.Validate(crate));
            crate.MassKg = 35m;
            Assert.Null(CargoService.Validate(crate));
        }

        [Fact]
        public void Validate_RejectsEmptyOrNegativeManifest()
        {
            var crate = Crate(CargoCustody.Depositor);
            crate.Manifest.Clear();
            Assert.Equal("RW_Reject_InvalidManifest", CargoService.Validate(crate));

            crate.Manifest.Add(new ManifestEntry { ThingDefName = "Steel", Count = -1 });
            Assert.Equal("RW_Reject_InvalidManifest", CargoService.Validate(crate));
        }

        [Fact]
        public void FacilityDestroyed_EjectsCratesOnceWithoutChangingCount()
        {
            var world = new WorldState();
            foreach (var id in new[] { "C1", "C2", "C3" })
            {
                var c = Crate(CargoCustody.Depositor, id: id);
                c.ContainerId = id == "C3" ? "Rack_2" : "Vault_1";
                world.Cargo[id] = c;
            }

            var ejected = CargoService.EjectFromContainer(world, "Vault_1");
            var again = CargoService.EjectFromContainer(world, "Vault_1");

            Assert.Equal(new[] { "C1", "C2" }, ejected.OrderBy(x => x));
            Assert.Empty(again);
            Assert.Equal(3, world.Cargo.Count);
            Assert.All(world.Cargo.Values, c => Assert.Equal(CargoCustody.Depositor, c.Custody));
            Assert.Equal("Rack_2", world.Cargo["C3"].ContainerId);
        }

        [Fact]
        public void CrateAtZeroHitPoints_IsDestroyedOtherwiseFullyReturnable()
        {
            var crate = Crate(CargoCustody.Depositor);

            Assert.False(CargoService.ApplyHitPoints(crate, 1));
            Assert.Equal(CargoCustody.Depositor, crate.Custody);
            Assert.True(CargoService.ApplyHitPoints(crate, 0));
            Assert.Equal(CargoCustody.Destroyed, crate.Custody);
            Assert.False(CargoService.ApplyHitPoints(crate, 0));
        }

        [Fact]
        public void ManifestBuilder_HitsTargetValueWithinMassLimit()
        {
            var goods = new List<Good>
            {
                new Good("Steel", null, 1.9m, 0.5m),
                new Good("Gold", null, 10m, 0.008m),
                new Good("ComponentIndustrial", null, 32m, 0.6m),
            };
            var rng = new DeterministicRandom(7);

            var crates = ManifestBuilder.Build(goods, new[] { 1000m, 1500m }, Limits.MaxCrateMassKg, rng);

            Assert.NotNull(crates);
            Assert.Equal(2, crates!.Count);
            foreach (var (manifest, value, mass) in crates)
            {
                Assert.True(mass <= Limits.MaxCrateMassKg, $"mass {mass}");
                Assert.True(manifest.All(m => m.Count > 0));
            }
            Assert.InRange(crates[0].Value, 900m, 1000m);
            Assert.InRange(crates[1].Value, 1350m, 1500m);
        }

        [Fact]
        public void ManifestBuilder_ReturnsNullWhenNoCombinationFits()
        {
            var goods = new List<Good> { new Good("Steel", null, 1.9m, 0.5m) };

            Assert.Null(ManifestBuilder.Build(goods, new[] { 5000m }, Limits.MaxCrateMassKg, new DeterministicRandom(1)));
        }

        [Fact]
        public void DeterministicRandom_IsReproducible()
        {
            var a = new DeterministicRandom(42);
            var b = new DeterministicRandom(42);
            for (int i = 0; i < 20; i++) Assert.Equal(a.Next(1000), b.Next(1000));
            Assert.InRange(new DeterministicRandom(3).NextDecimal(), 0m, 1m);
        }
    }
}
