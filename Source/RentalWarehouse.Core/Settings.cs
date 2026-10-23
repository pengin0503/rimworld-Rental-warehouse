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
        public long DurationTicks;
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
            DurationTicks = 6 * Time.TicksPerDay,
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
            DurationTicks = 6 * Time.TicksPerDay,
            MinTotalValue = 2000m,
            MaxTotalValue = 6000m,
            DailyBase = 12m,
            DailyValueRate = 0.004m,
            CompletionRate = 0.50m,
            RequiredStorage = StorageKind.Vault,
            RaidPointFactor = 1.0m,
        };

        public static ContractParameters For(ContractKind kind) => kind == ContractKind.HighRisk ? HighRisk : Normal;
    }

    /// <summary>Business-wide limits from spec 4/5.</summary>
    public static class Limits
    {
        public const int MaxActiveContracts = 3;
        public const int MaxOpenOffers = 4;
        public const int OffersPerBatch = 2;
        public const long FirstOfferDelay = Time.TicksPerDay;
        public const long OfferInterval = 2 * Time.TicksPerDay;
        public const long OfferLifetime = 3 * Time.TicksPerDay;
        public const long DeliveryDelay = Time.TicksPerDay;
        public const long StorageDeadline = Time.TicksPerDay;
        public const long ReturnGrace = 3 * Time.TicksPerDay;
        public const long HostileReturnWindow = 3 * Time.TicksPerDay;
        public const int CrateMaxHitPoints = 100;
        public const int MinCrates = 1;
        public const int MaxCrates = 4;
        public const decimal MaxCrateMassKg = 35m;
        public const int RefuseGoodwill = -20;
        public const long RefuseBlockTicks = 15 * Time.TicksPerDay;
        public const int CompletionGoodwill = 3;
        public const int EmbezzleGoodwill = -30;
        public const int LossGoodwillScale = 10;
        public const decimal LossCompensationRate = 0.25m;
        public const decimal EmbezzleCompensationRate = 1.0m;
        public const int CancellationFeeDays = 2;
        public static readonly long[] PaymentDays = { 2 * Time.TicksPerDay, 4 * Time.TicksPerDay };
    }
}
