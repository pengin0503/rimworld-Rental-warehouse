using System.Collections.Generic;

namespace RentalWarehouse.Core
{
    /// <summary>Game-independent constants. The game adapter checks <see cref="TicksPerDay"/> against GenDate.TicksPerDay.</summary>
    public static class CoreTime
    {
        public const long TicksPerDay = 60000;
        public const long CheckIntervalTicks = 250;
    }

    public enum ContractKind
    {
        Normal,
        HighRisk,
    }

    public enum StorageKind
    {
        Rack,
        Vault,
    }

    public enum ContractPhase
    {
        Offered,
        AwaitingDelivery,
        AwaitingStorage,
        Active,
        Returning,
        Settled,
        Cancelled,
        SystemCancelled,
    }

    public enum CargoCustody
    {
        /// <summary>Owned by another faction and in the player's (or an NPC vault's) care.</summary>
        Depositor,
        /// <summary>Owned by the player (stolen from an NPC vault).</summary>
        Player,
        StolenByEnemy,
        Returned,
        Destroyed,
    }

    /// <summary>Why a contract crate did not come back.</summary>
    public enum LossCause
    {
        None,
        /// <summary>Destroyed, carried off the map by raiders, or left unreturned.</summary>
        Lost,
        /// <summary>Opened by the player on purpose.</summary>
        Embezzled,
        /// <summary>Removed for a reason outside the player's control (faction gone, map vanished).</summary>
        SystemRemoved,
    }

    public enum ReturnReason
    {
        None,
        /// <summary>Duration completed.</summary>
        Matured,
        /// <summary>Player asked for an early return after arrival.</summary>
        EarlyByPlayer,
        /// <summary>Crates could not be stored within the storage deadline, or were lost before storage.</summary>
        StorageFailed,
        /// <summary>Depositor became hostile: penalty-free early return window.</summary>
        DepositorHostile,
    }

    public enum SettlementCause
    {
        None,
        Completed,
        PartialLoss,
        TotalLoss,
        EarlyReturn,
        StorageFailed,
        DepositorHostile,
        MapAbandoned,
        SystemCancelled,
    }

    public sealed class ManifestEntry
    {
        public string ThingDefName = "";
        public string? StuffDefName;
        /// <summary>Quality as the game's integer value; validated game-side.</summary>
        public int? Quality;
        public int Count;

        public ManifestEntry Clone() => (ManifestEntry)MemberwiseClone();
    }

    public sealed class CargoRecord
    {
        public string CargoId = "";
        public string? ContractId;
        public string? SiteId;
        public string OwnerFactionId = "";
        /// <summary>Original owner kept after theft so stolen crates stay traceable.</summary>
        public string? OriginalOwnerFactionId;
        public CargoCustody Custody;
        public List<ManifestEntry> Manifest = new List<ManifestEntry>();
        public decimal DeclaredValue;
        public decimal MassKg;
        public int HitPoints = Limits.CrateMaxHitPoints;
        public StorageKind RequiredStorage;
        public string? ContainerId;
        public bool Opened;
        public bool Stolen;

        public CargoRecord Clone()
        {
            var copy = (CargoRecord)MemberwiseClone();
            copy.Manifest = Manifest.ConvertAll(m => m.Clone());
            return copy;
        }
    }

    public sealed class ContractTerms
    {
        public string ContractId = "";
        public string DepositorFactionId = "";
        /// <summary>Faction hostile to both the player and the depositor; null when none exists.</summary>
        public string? PursuerFactionId;
        public ContractKind Kind;
        public List<string> CargoIds = new List<string>();
        /// <summary>Declared value per crate, parallel to <see cref="CargoIds"/>.</summary>
        public List<decimal> CargoValues = new List<decimal>();
        public StorageKind RequiredStorage;
        public long DurationTicks;
        public decimal DailyBase;
        public decimal DailyValueRate;
        public decimal CompletionRate;
        public decimal FeeMultiplier = 1m;
        public decimal RaidScale = 1m;
        public int ScheduledRaidCount;
        /// <summary>True when the Empire is the depositor: its settlements may demote two ranks instead of one (spec 3.4).</summary>
        public bool DepositorIsEmpire;
        public decimal NominalTotalReward;
        /// <summary>Share (0–0.30) of the full-term fee paid into the claimable balance when the term starts.</summary>
        public decimal PrepaymentRate;
        public int Seed;
        public int SettingsVersion;
        public long OfferedTick;
        public long OfferExpiresTick;

        public decimal TotalValue
        {
            get
            {
                decimal sum = 0m;
                foreach (var v in CargoValues) sum += v;
                return sum;
            }
        }

        public ContractTerms Clone()
        {
            var copy = (ContractTerms)MemberwiseClone();
            copy.CargoIds = new List<string>(CargoIds);
            copy.CargoValues = new List<decimal>(CargoValues);
            return copy;
        }
    }

    public sealed class ContractCargoState
    {
        public string CargoId = "";
        public decimal DeclaredValue;
        /// <summary>Ticks spent in valid storage while Active. Fees derive from this to avoid rounding drift.</summary>
        public long StoredTicks;
        public decimal AccruedFee;
        public bool ValidlyStored;
        public bool Delivered;
        public bool Returned;
        public LossCause Loss;
        public long? LossTick;

        public ContractCargoState Clone() => (ContractCargoState)MemberwiseClone();
    }

    public enum RaidSlotState
    {
        Pending,
        Started,
        Skipped,
    }

    public sealed class RaidSlot
    {
        public int Index;
        public long PlannedTick;
        public RaidSlotState State;
        public long? StartedTick;

        public RaidSlot Clone() => (RaidSlot)MemberwiseClone();
    }

    public sealed class ContractState
    {
        public ContractTerms Terms = new ContractTerms();
        public ContractPhase Phase;
        public string MapId = "";
        public long AcceptedTick;
        public long DeliveryTick;
        public long? DeliveredTick;
        public long? ActiveTick;
        public long? EndTick;
        public long? ReturnDeadlineTick;
        public long LastAccrualTick;
        public List<ContractCargoState> CargoStates = new List<ContractCargoState>();
        public decimal AccruedFee;
        /// <summary>Whole silver moved into the claimable balance before settlement (the prepayment).</summary>
        public decimal PostedFee;
        /// <summary>Whole silver actually claimed as silver so far.</summary>
        public decimal ClaimedFee;
        /// <summary>Claimable balance currently held for this contract.</summary>
        public decimal Receivable;
        public int StartedRaids;
        public decimal BaselineThreatPoints;
        public List<RaidSlot> RaidPlan = new List<RaidSlot>();
        public ReturnReason ReturnReason;
        public int ReturnPodsRequested;
        public string? ActiveReturnPodId;
        public SettlementCause SettlementCause;
        public SettlementResult? Settlement;
        /// <summary>True once a settlement debt has been entered in <see cref="WorldState.Debts"/>.</summary>
        public bool DebtRecorded;
        public string? CancelReason;

        public ContractCargoState? Cargo(string cargoId) => CargoStates.Find(c => c.CargoId == cargoId);

        public bool IsTerminal =>
            Phase == ContractPhase.Settled || Phase == ContractPhase.Cancelled || Phase == ContractPhase.SystemCancelled;

        public ContractState Clone()
        {
            var copy = (ContractState)MemberwiseClone();
            copy.Terms = Terms.Clone();
            copy.CargoStates = CargoStates.ConvertAll(c => c.Clone());
            copy.RaidPlan = RaidPlan.ConvertAll(r => r.Clone());
            copy.Settlement = Settlement?.Clone();
            return copy;
        }
    }

    public sealed class FacilitySnapshot
    {
        public string FacilityId = "";
        public StorageKind Kind;
        public int FreeSlots;
        public bool IsValid = true;
    }

    public sealed class StorageSnapshot
    {
        public string MapId = "";
        public List<FacilitySnapshot> Facilities = new List<FacilitySnapshot>();
        /// <summary>Crates currently inside a valid facility that satisfies their required storage.</summary>
        public HashSet<string> ValidStoredCargoIds = new HashSet<string>();

        public int FreeSlots(StorageKind kind)
        {
            int sum = 0;
            foreach (var f in Facilities)
            {
                if (f.Kind == kind && f.IsValid) sum += f.FreeSlots;
            }
            return sum;
        }
    }

    public sealed class Debt
    {
        public string DebtId = "";
        public string ContractId = "";
        public string FactionId = "";
        public decimal Amount;
        public long CreatedTick;

        public Debt Clone() => (Debt)MemberwiseClone();
    }

    public sealed class SettlementResult
    {
        public decimal AccruedFee;
        public decimal CompletionBonus;
        public decimal CancellationFee;
        public decimal Compensation;
        public decimal PreviouslyClaimed;
        public decimal Credit;
        public decimal Debt;
        public int GoodwillDelta;
        /// <summary>Trust points this settlement adds or removes (spec 3.2); applied once by an ApplyTrust effect.</summary>
        public int TrustDelta;
        public decimal ReturnValueRate;

        public SettlementResult Clone() => (SettlementResult)MemberwiseClone();
    }

    public enum EffectKind
    {
        DeliverCargo,
        SpawnSilver,
        ConsumeSilver,
        ConsumeCargo,
        ApplyGoodwill,
        CreateReturnPod,
        StartRaid,
        CreateSite,
        SpawnReinforcement,
        CloseSite,
        RemoveCargo,
        ReleaseVaultCargo,
        Notify,
        /// <summary>Adds Amount trust points; Payload["empire"] is "1" when the depositor is the Empire (spec 3.5).</summary>
        ApplyTrust,
    }

    public sealed class EffectRequest
    {
        public string EffectId = "";
        public EffectKind Kind;
        public string EntityId = "";
        public decimal Amount;
        /// <summary>Kind-specific data: faction id, map id, cargo ids, message key…</summary>
        public Dictionary<string, string> Payload = new Dictionary<string, string>();

        public EffectRequest Clone()
        {
            var copy = (EffectRequest)MemberwiseClone();
            copy.Payload = new Dictionary<string, string>(Payload);
            return copy;
        }

        public override string ToString() => $"{Kind}:{EffectId}";
    }

    public sealed class Decision<T>
    {
        public bool Success;
        public string? ReasonKey;
        public T NewState = default!;
        public List<EffectRequest> Effects = new List<EffectRequest>();

        public static Decision<T> Ok(T state, List<EffectRequest>? effects = null) =>
            new Decision<T> { Success = true, NewState = state, Effects = effects ?? new List<EffectRequest>() };

        public static Decision<T> Fail(string reasonKey, T state) =>
            new Decision<T> { Success = false, ReasonKey = reasonKey, NewState = state };
    }

    public sealed class WorldState
    {
        public const int CurrentSchemaVersion = 3;

        public int SchemaVersion = CurrentSchemaVersion;
        public int WorldSeed;
        public bool BusinessOpen;
        public long? BusinessStartTick;
        public long? NextOfferTick;
        public long? NextSiteTick;
        /// <summary>Index of the next offer batch; seeds that batch and names its contracts (RWC-batch-slot).</summary>
        public int NextOfferBatch;
        /// <summary>Index of the next vault intel report; seeds that report and names its site (RWS-index).</summary>
        public int NextSiteIndex;
        public int SequenceCounter;
        /// <summary>Trust points (spec 3.1). The rank is computed from them and the rank table, never stored.</summary>
        public int TrustPoints;
        /// <summary>The rank last announced to the player, so a change is reported once.</summary>
        public int TrustAnnouncedRank;
        public List<ContractState> Offers = new List<ContractState>();
        public List<ContractState> Contracts = new List<ContractState>();
        public List<SiteRecord> Sites = new List<SiteRecord>();
        /// <summary>Contracts set aside because their ID collided with another (spec 8); never processed.</summary>
        public List<ContractState> Quarantine = new List<ContractState>();
        public bool Initialized;
        public long? InstalledTick;
        /// <summary>Contract maps that disappeared, with the cause seen at the time (spec 8).</summary>
        public Dictionary<string, MapRemovalCause> RemovedMaps = new Dictionary<string, MapRemovalCause>();
        public Dictionary<string, CargoRecord> Cargo = new Dictionary<string, CargoRecord>();
        public List<Debt> Debts = new List<Debt>();
        public Dictionary<string, long> FactionBlockedUntil = new Dictionary<string, long>();
        /// <summary>Last tick hostiles were seen per map; a raid "ended" when they stopped being seen (spec 6).</summary>
        public Dictionary<string, long> LastHostileTick = new Dictionary<string, long>();
        public WarehouseSettings Settings = new WarehouseSettings();
        public int SettingsVersion;
        public EffectLedger Effects = new EffectLedger();
        public List<EffectRequest> PendingEffects = new List<EffectRequest>();
        /// <summary>Runtime only (not public, so never serialised): set while the effect queue is being processed.</summary>
        internal bool ProcessingEffects;

        public ContractState? Contract(string contractId) => Contracts.Find(c => c.Terms.ContractId == contractId);

        /// <summary>Total claimable balance across contracts, optionally limited to one map.</summary>
        public decimal ReceivableBalance(string? mapId = null)
        {
            decimal sum = 0m;
            foreach (var c in Contracts)
            {
                if (mapId == null || c.MapId == mapId) sum += c.Receivable;
            }
            return sum;
        }

        /// <summary>
        /// Whether <paramref name="c"/>'s balance can be claimed at <paramref name="mapId"/>: contracts of that map, and of maps
        /// that no longer exist, whose earned fees would otherwise be unreachable (spec 8 keeps them).
        /// </summary>
        public bool ClaimableAt(ContractState c, string mapId) => c.MapId == mapId || RemovedMaps.ContainsKey(c.MapId);

        /// <summary>Balance that a claim at <paramref name="mapId"/> pays out.</summary>
        public decimal ClaimableBalance(string mapId)
        {
            decimal sum = 0m;
            foreach (var c in Contracts)
            {
                if (ClaimableAt(c, mapId)) sum += c.Receivable;
            }
            return sum;
        }

        public string NextSequenceId(string prefix)
        {
            SequenceCounter++;
            return prefix + "-" + SequenceCounter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
