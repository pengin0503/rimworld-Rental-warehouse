using RentalWarehouse.Core;
using Xunit;

namespace RentalWarehouse.Core.Tests
{
    public class SettingsTests
    {
        [Theory]
        [InlineData(0.1, 0.25)]
        [InlineData(5.0, 4.0)]
        [InlineData(1.12, 1.0)]
        [InlineData(1.13, 1.25)]
        [InlineData(2.0, 2.0)]
        public void Multiplier_IsClampedAndSnappedToQuarterSteps(double input, double expected)
        {
            Assert.Equal((decimal)expected, WarehouseSettings.NormalizeMultiplier((decimal)input));
        }

        [Fact]
        public void Defaults_MatchSpec()
        {
            var s = new WarehouseSettings();
            Assert.True(s.NormalContractsEnabled);
            Assert.True(s.HighRiskContractsEnabled);
            Assert.True(s.VaultSitesEnabled);
            Assert.False(s.NonHostileVaultSitesEnabled);
            Assert.Equal(1.0m, s.FeeMultiplier);
            Assert.Equal(1.0m, s.RaidScale);
        }

        [Fact]
        public void ChangedSettings_DoNotAffectAcceptedTerms()
        {
            var settings = new WarehouseSettings { FeeMultiplier = 1.0m };
            var terms = ContractTermsFactory.Create(ContractParameters.Normal, "K", "F", null, new[] { ("K-0", 1000m) }, 0, Fixtures.Term, settings, 0, 1, 0);
            settings.FeeMultiplier = 3.0m;

            Assert.Equal(1.0m, terms.FeeMultiplier);
            Assert.Equal(10m, Accounting.DailyFee(terms, 1000m));
        }
    }
}
