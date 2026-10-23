using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// World-wide ledger holder (spec 9). Owns the Core <see cref="WorldState"/>; all business state lives there, not in
    /// game references. Also keeps a runtime index from cargo ID to crate Thing so nothing scans all Things.
    /// </summary>
    public class WarehouseWorldComponent : WorldComponent
    {
        public WorldState State = new WorldState();

        private readonly Dictionary<string, Thing> crates = new Dictionary<string, Thing>();

        /// <summary>A ledger that could not be read is kept verbatim and written back, so a bad load never erases it.</summary>
        private string unreadablePayload;
        private int unreadableSchema;

        public WarehouseWorldComponent(World world) : base(world)
        {
        }

        public bool LedgerUnreadable => unreadablePayload != null;

        public override void ExposeData()
        {
            base.ExposeData();
            string payload = null;
            int schema = 0;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (unreadablePayload != null)
                {
                    payload = unreadablePayload;
                    schema = unreadableSchema;
                }
                else
                {
                    var saved = StateCodec.Export(State);
                    payload = saved.Payload;
                    schema = saved.SchemaVersion;
                }
            }
            Scribe_Values.Look(ref schema, "schemaVersion");
            Scribe_Values.Look(ref payload, "ledger");
            if (Scribe.mode == LoadSaveMode.LoadingVars) Load(new SavedState { SchemaVersion = schema, Payload = payload ?? "" });
        }

        private void Load(SavedState saved)
        {
            try
            {
                State = StateCodec.Import(Migration.Upgrade(saved));
                unreadablePayload = null;
            }
            catch (StateCodecException e)
            {
                unreadablePayload = saved.Payload;
                unreadableSchema = saved.SchemaVersion;
                State = new WorldState();
                Log.Error("[RentalWarehouse] " + e.ReasonKey + ": " + e.Message + " The ledger is kept unchanged in the save; the warehouse is paused.");
            }
        }

        /// <summary>Mid-game install or any load: initialise once, then stay idle until the business opens (spec 9).</summary>
        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            if (LedgerUnreadable) return;
            LifecycleService.Initialize(State, Gen.HashCombineInt(Find.World.info.Seed.GetHashCode(), 0x52574857), Ids.Now);
        }

        public override void WorldComponentTick()
        {
            if (LedgerUnreadable) return;
            if (Find.TickManager.TicksGame % (int)CoreTime.CheckIntervalTicks != 0) return;
            Tick();
        }

        /// <summary>One 250-tick business step. Later tasks add contract, raid and site processing here.</summary>
        public void Tick()
        {
            long now = Ids.Now;
            RentalWarehouseSettings.SyncInto(State);
            OfferGenerator.Tick(this, now);
            StorageQuest.Tick(this, now);
            ThreatAdapter.Tick(this, now);
            VaultDiscovery.Tick(this, now);
            VaultSiteHooks.Tick(this, now);
            VaultLootAdapter.Tick(this, now);
            LifecycleAdapter.Tick(this, now);
            EffectDispatcher.ProcessQueue(this);
        }

        public void Enqueue(IEnumerable<EffectRequest> effects, bool applyNow = true)
        {
            EffectQueue.EnqueueAll(State, effects);
            if (applyNow) EffectDispatcher.ProcessQueue(this);
        }

        public static WarehouseWorldComponent Instance => Find.World?.GetComponent<WarehouseWorldComponent>();

        /// <summary>
        /// A second live Thing claiming an already registered cargo ID is isolated (spec 8 "重複ID"): it loses the ID,
        /// so it has no value, no contract and cannot be returned or opened for loot.
        /// </summary>
        public void RegisterCrate(string cargoId, Thing crate)
        {
            if (cargoId == null || crate == null) return;
            if (crates.TryGetValue(cargoId, out var existing) && existing != crate && !existing.Destroyed)
            {
                var comp = CargoAdapter.Comp(crate);
                if (comp != null) comp.cargoId = null;
                Diagnostics.Report("RW_Diag_DuplicateCrate", cargoId);
                return;
            }
            crates[cargoId] = crate;
        }

        public void UnregisterCrate(string cargoId, Thing crate)
        {
            if (cargoId != null && crates.TryGetValue(cargoId, out var current) && current == crate) crates.Remove(cargoId);
        }

        public Thing FindCrate(string cargoId)
        {
            if (cargoId == null || !crates.TryGetValue(cargoId, out var crate)) return null;
            return crate.Destroyed ? null : crate;
        }

        public IEnumerable<KeyValuePair<string, Thing>> AllCrates => crates;

        public void SetBusinessOpen(bool open)
        {
            OfferService.SetBusinessOpen(State, open, Ids.Now);
        }

        /// <summary>A crate was destroyed by something other than the warehouse itself.</summary>
        public void Notify_CrateDestroyed(string cargoId)
        {
            if (cargoId == null || !State.Cargo.TryGetValue(cargoId, out var record)) return;
            CargoService.ApplyHitPoints(record, 0);
            var contract = record.ContractId == null ? null : State.Contract(record.ContractId);
            if (contract == null) return;
            Enqueue(ContractEngine.MarkLost(contract, cargoId, LossCause.Lost, Ids.Now).Effects);
            PaymentService.SyncDebts(State, Ids.Now);
        }

        /// <summary>Deliberate opening of a crate held for another faction: spec 3 "横領".</summary>
        public void Embezzle(string cargoId)
        {
            if (!State.Cargo.TryGetValue(cargoId, out var record) || record.ContractId == null) return;
            var contract = State.Contract(record.ContractId);
            if (contract == null) return;
            var crate = FindCrate(cargoId);
            if (crate == null || !crate.Spawned) return;
            var lost = ContractEngine.MarkLost(contract, cargoId, LossCause.Embezzled, Ids.Now);
            if (!lost.Success) return;
            var effects = new List<EffectRequest>(CargoService.Open(record, "open:" + cargoId).Effects);
            effects.AddRange(lost.Effects);
            Enqueue(effects);
            PaymentService.SyncDebts(State, Ids.Now);
        }

        /// <summary>Opening a crate stolen from an NPC vault: the contents are the reward.</summary>
        public void OpenStolen(string cargoId)
        {
            if (!State.Cargo.TryGetValue(cargoId, out var record) || record.Custody != CargoCustody.Player) return;
            var crate = FindCrate(cargoId);
            if (crate == null || !crate.Spawned) return;
            Enqueue(CargoService.Open(record, "open:" + cargoId).Effects);
        }
    }
}
