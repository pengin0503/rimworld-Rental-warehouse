using System;

namespace RentalWarehouse.Core
{
    /// <summary>Player-facing settings (spec 8). Changes apply to new offers and sites only.</summary>
    public sealed class WarehouseSettings
    {
        public const decimal MultiplierMin = 0.25m;
        public const decimal MultiplierMax = 4.0m;
        public const decimal MultiplierStep = 0.25m;

        public bool NormalContractsEnabled = true;
        public bool HighRiskContractsEnabled = true;
        public bool VaultSitesEnabled = true;
        public bool NonHostileVaultSitesEnabled = false;
        public decimal FeeMultiplier = 1.0m;
        public decimal RaidScale = 1.0m;

        public WarehouseSettings Clone() => (WarehouseSettings)MemberwiseClone();

        /// <summary>Clamps to 0.25–4.0 and snaps to the 0.25 grid.</summary>
        public static decimal NormalizeMultiplier(decimal value)
        {
            if (value < MultiplierMin) value = MultiplierMin;
            if (value > MultiplierMax) value = MultiplierMax;
            return Math.Round(value / MultiplierStep, MidpointRounding.AwayFromZero) * MultiplierStep;
        }
    }

    /// <summary>Fixed contract parameters from spec 5. Not editable in the first release UI.</summary>
    public sealed class ContractParameters
    {
        public ContractKind Kind;
        /// <summary>Term length is drawn per offer, in whole days within this range (inclusive).</summary>
        public int MinDurationDays;
        public int MaxDurationDays;
        public decimal MinTotalValue;
        public decimal MaxTotalValue;
        public decimal DailyBase;
        public decimal DailyValueRate;
        public decimal CompletionRate;
        public StorageKind RequiredStorage;
        /// <summary>Threat point factor relative to the baseline P at activation.</summary>
        public decimal RaidPointFactor;

        public static readonly ContractParameters Normal = new ContractParameters
        {
            Kind = ContractKind.Normal,
            MinDurationDays = 3,
            MaxDurationDays = 12,
            MinTotalValue = 500m,
            MaxTotalValue = 2000m,
            DailyBase = 8m,
            DailyValueRate = 0.002m,
            CompletionRate = 0.20m,
            RequiredStorage = StorageKind.Rack,
            RaidPointFactor = 0.6m,
        };

        public static readonly ContractParameters HighRisk = new ContractParameters
        {
            Kind = ContractKind.HighRisk,
            MinDurationDays = 3,
            MaxDurationDays = 12,
            MinTotalValue = 2000m,
            MaxTotalValue = 6000m,
            DailyBase = 12m,
            DailyValueRate = 0.004m,
            CompletionRate = 0.50m,
            RequiredStorage = StorageKind.Vault,
            RaidPointFactor = 1.0m,
        };

        public static ContractParameters For(ContractKind kind) => kind == ContractKind.HighRisk ? HighRisk : Normal;

        public long DrawDurationTicks(DeterministicRandom rng) => rng.Range(MinDurationDays, MaxDurationDays) * CoreTime.TicksPerDay;
    }

    /// <summary>Business-wide limits from spec 4/5.</summary>
    public static class Limits
    {
        public const int MaxActiveContracts = 3;
        public const int MaxOpenOffers = 4;
        public const int OffersPerBatch = 2;
        public const long FirstOfferDelay = CoreTime.TicksPerDay;
        /// <summary>Gap between offer batches, drawn per batch (inclusive, in ticks).</summary>
        public const long MinOfferInterval = CoreTime.TicksPerDay;
        public const long MaxOfferInterval = 3 * CoreTime.TicksPerDay;
        public const long OfferLifetime = 3 * CoreTime.TicksPerDay;
        public const long DeliveryDelay = CoreTime.TicksPerDay;
        public const long StorageDeadline = CoreTime.TicksPerDay;
        public const long ReturnGrace = 3 * CoreTime.TicksPerDay;
        public const long HostileReturnWindow = 3 * CoreTime.TicksPerDay;
        public const int CrateMaxHitPoints = 100;
        /// <summary>Mass of the empty crate (matches the ThingDef's base Mass); counts toward the 35 kg limit.</summary>
        public const decimal CrateTareKg = 2m;
        public const int MinCrates = 1;
        public const int MaxCrates = 4;
        public const decimal MaxCrateMassKg = 35m;
        public const int RefuseGoodwill = -20;
        public const long RefuseBlockTicks = 15 * CoreTime.TicksPerDay;
        public const int CompletionGoodwill = 3;
        public const int EmbezzleGoodwill = -20;
        public const int LossGoodwillScale = 10;
        public const decimal LossCompensationRate = 0.25m;
        public const decimal EmbezzleCompensationRate = 1.0m;
        public const int CancellationFeeDays = 2;
        /// <summary>Each offer pays 0–30% of its full-term fee in advance, when the term starts (whole percent); the rest is paid after settlement.</summary>
        public const int MaxPrepaymentPercent = 30;
    }

    /// <summary>NPC vault site parameters (spec 7).</summary>
    public static class VaultSiteParameters
    {
        public const long FirstDiscoveryDelay = 2 * CoreTime.TicksPerDay;
        /// <summary>Gap between vault intel reports, drawn per report (inclusive, in ticks).</summary>
        public const long MinDiscoveryInterval = 3 * CoreTime.TicksPerDay;
        public const long MaxDiscoveryInterval = 12 * CoreTime.TicksPerDay;
        public const int MaxUnvisited = 3;
        public const long UnvisitedLifetime = 15 * CoreTime.TicksPerDay;
        public const long WithdrawalAfterFirstEntry = 5 * CoreTime.TicksPerDay;
        public const int CratesPerVault = 4;
        public const long AlarmDelay = CoreTime.TicksPerDay / 4;
        public const decimal ReinforcementFraction = 0.5m;
        public const decimal ForcedOpenLossChance = 0.30m;
        public const decimal CraftingSpeedPerLevel = 0.05m;
        /// <summary>Spec 7: goodwill a non-hostile owner is set to when attacked (design value; the game's own threshold wins).</summary>
        public const int HostileGoodwillTarget = -75;

        public static int Crates(SiteTier tier) => tier == SiteTier.Small ? 2 : tier == SiteTier.Medium ? 4 : 6;
        public static decimal MinValue(SiteTier tier) => tier == SiteTier.Small ? 1000m : tier == SiteTier.Medium ? 3000m : 6000m;
        public static decimal MaxValue(SiteTier tier) => tier == SiteTier.Small ? 2000m : tier == SiteTier.Medium ? 5000m : 9000m;
        public static decimal GuardFactor(SiteTier tier) => tier == SiteTier.Small ? 0.8m : tier == SiteTier.Medium ? 1.2m : 1.6m;
        public static long UnlockWork(SiteTier tier) => tier == SiteTier.Small ? 6000 : tier == SiteTier.Medium ? 12000 : 18000;
    }
}
