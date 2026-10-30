using System;
using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    public enum MapRemovalCause
    {
        /// <summary>Gone without the player abandoning it (another mod, a script…).</summary>
        Unknown,
        AbandonedByPlayer,
    }

    /// <summary>What currently exists in the game, for reconciliation. Built by the game adapter.</summary>
    public sealed class WorldSnapshot
    {
        public HashSet<string> MapIds = new HashSet<string>();
        /// <summary>Maps known to be gone, with why. A map absent from both sets is treated as merely not loaded.</summary>
        public Dictionary<string, MapRemovalCause> RemovedMapCauses = new Dictionary<string, MapRemovalCause>();
        /// <summary>Factions that still exist and are not defeated.</summary>
        public HashSet<string> FactionIds = new HashSet<string>();
        public HashSet<string> HostileFactionIds = new HashSet<string>();
        /// <summary>Known ThingDef names; empty means "not checked".</summary>
        public HashSet<string> DefNames = new HashSet<string>();
    }

    /// <summary>
    /// Spec 8 exceptions, applied once per cause: vanished depositor, hostile depositor, abandoned or vanished map,
    /// missing defs, duplicate IDs, vanished creditor. Also trims finished history so the ledger does not grow without bound.
    /// </summary>
    public static class LifecycleService
    {
        public const int SettledHistoryKept = 20;
        public const long ClosedSiteRetention = 10 * CoreTime.TicksPerDay;
        public const long FinishedEffectRetention = 30 * CoreTime.TicksPerDay;

        public static Decision<WorldState> Reconcile(WorldState world, WorldSnapshot snapshot, long now)
        {
            var effects = new List<EffectRequest>();
            QuarantineDuplicates(world, effects);
            WithdrawOffers(world, snapshot);

            foreach (var c in world.Contracts.ToArray())
            {
                if (c.IsTerminal) continue;
                string? missing = MissingDef(world, c.Terms.CargoIds, snapshot);
                if (missing != null)
                {
                    effects.AddRange(ContractEngine.SystemCancel(c, "RW_Cancel_MissingDefs", now).Effects);
                    effects.Add(Diagnostic("RW_Diag_MissingDefs", c.Terms.ContractId + ": " + missing));
                    continue;
                }
                if (!snapshot.MapIds.Contains(c.MapId) && snapshot.RemovedMapCauses.TryGetValue(c.MapId, out var cause))
                {
                    if (cause == MapRemovalCause.AbandonedByPlayer) effects.AddRange(Abandon(c, now));
                    else
                    {
                        effects.AddRange(ContractEngine.SystemCancel(c, "RW_Cancel_MapVanished", now).Effects);
                        effects.Add(Diagnostic("RW_Diag_MapVanished", c.Terms.ContractId));
                    }
                    continue;
                }
                if (!snapshot.FactionIds.Contains(c.Terms.DepositorFactionId))
                {
                    effects.AddRange(ContractEngine.SystemCancel(c, "RW_Cancel_DepositorGone", now).Effects);
                    effects.Add(Notice(c, "RW_Msg_DepositorGone"));
                    continue;
                }
                if (snapshot.HostileFactionIds.Contains(c.Terms.DepositorFactionId))
                {
                    if (c.Phase == ContractPhase.AwaitingDelivery && !c.CargoStates.Exists(x => x.Delivered))
                    {
                        effects.AddRange(ContractEngine.Cancel(c, now, "RW_Cancel_DepositorHostile").Effects);
                        effects.Add(Notice(c, "RW_Msg_CancelledDepositorHostile"));
                    }
                    else if (c.Phase == ContractPhase.Active || c.Phase == ContractPhase.AwaitingStorage)
                    {
                        effects.AddRange(ContractEngine.RequestEarlyReturn(c, ReturnReason.DepositorHostile, now).Effects);
                        effects.Add(new EffectRequest
                        {
                            EffectId = c.Terms.ContractId + ":hostile-notice",
                            Kind = EffectKind.Notify,
                            EntityId = c.Terms.ContractId,
                            Payload = { ["key"] = "RW_Msg_DepositorHostile" },
                        });
                    }
                }
            }

            world.Debts.RemoveAll(d => !snapshot.FactionIds.Contains(d.FactionId));
            Prune(world, now);
            return Decision<WorldState>.Ok(world, effects);
        }

        private static IEnumerable<EffectRequest> Abandon(ContractState c, long now)
        {
            if (c.Phase == ContractPhase.AwaitingDelivery && !c.CargoStates.Exists(x => x.Delivered))
            {
                var cancelled = ContractEngine.Cancel(c, now, "RW_Cancel_MapAbandoned").Effects;
                cancelled.Add(Notice(c, "RW_Msg_CancelledMapAbandoned"));
                return cancelled;
            }
            var effects = new List<EffectRequest>();
            foreach (var cs in c.CargoStates)
            {
                if (c.IsTerminal) break;
                if (cs.Returned || cs.Loss != LossCause.None) continue;
                effects.AddRange(ContractEngine.MarkLost(c, cs.CargoId, LossCause.Lost, now).Effects);
            }
            if (c.Phase == ContractPhase.Settled) c.SettlementCause = SettlementCause.MapAbandoned;
            return effects;
        }

        private static string? MissingDef(WorldState world, List<string> cargoIds, WorldSnapshot snapshot)
        {
            if (snapshot.DefNames.Count == 0) return null;
            foreach (var id in cargoIds)
            {
                if (!world.Cargo.TryGetValue(id, out var record)) continue;
                foreach (var m in record.Manifest)
                {
                    if (!snapshot.DefNames.Contains(m.ThingDefName)) return m.ThingDefName;
                    if (m.StuffDefName != null && !snapshot.DefNames.Contains(m.StuffDefName)) return m.StuffDefName;
                }
            }
            return null;
        }

        private static void QuarantineDuplicates(WorldState world, List<EffectRequest> effects)
        {
            var seen = new HashSet<string>();
            for (int i = 0; i < world.Contracts.Count; i++)
            {
                var c = world.Contracts[i];
                if (seen.Add(c.Terms.ContractId)) continue;
                world.Contracts.RemoveAt(i--);
                world.Quarantine.Add(c);
                effects.Add(Diagnostic("RW_Diag_DuplicateId", c.Terms.ContractId, world.Quarantine.Count.ToString(CultureInfo.InvariantCulture)));
            }
        }

        private static void WithdrawOffers(WorldState world, WorldSnapshot snapshot)
        {
            for (int i = world.Offers.Count - 1; i >= 0; i--)
            {
                var t = world.Offers[i].Terms;
                bool gone = !snapshot.FactionIds.Contains(t.DepositorFactionId) || snapshot.HostileFactionIds.Contains(t.DepositorFactionId)
                    || MissingDef(world, t.CargoIds, snapshot) != null;
                if (!gone) continue;
                foreach (var id in t.CargoIds) world.Cargo.Remove(id);
                world.Offers.RemoveAt(i);
            }
        }

        /// <summary>Tells the player once why a contract ended without their action (spec 8); the ID makes repeats no-ops.</summary>
        private static EffectRequest Notice(ContractState c, string key) => new EffectRequest
        {
            EffectId = c.Terms.ContractId + ":system-end-notice",
            Kind = EffectKind.Notify,
            EntityId = c.Terms.ContractId,
            Payload = { ["key"] = key },
        };

        private static EffectRequest Diagnostic(string key, string subject, string? salt = null) => new EffectRequest
        {
            EffectId = "diag:" + key + ":" + subject + (salt == null ? "" : ":" + salt),
            Kind = EffectKind.Notify,
            EntityId = subject,
            Payload = { ["key"] = key, ["diagnostic"] = "true" },
        };

        /// <summary>
        /// Keeps the newest settled contracts for the history view and drops older ones with their crate records and
        /// effect IDs. Safe because effect IDs embed the contract/site ID and new IDs are never reused.
        /// </summary>
        private static void Prune(WorldState world, long now)
        {
            // Most passes have nothing old enough to drop, so the pending-effect IDs are only collected when a prefix is asked about.
            List<string>? pendingIds = null;
            bool Pending(string prefix)
            {
                if (pendingIds == null)
                {
                    pendingIds = new List<string>(world.PendingEffects.Count);
                    foreach (var e in world.PendingEffects) pendingIds.Add(e.EffectId);
                }
                foreach (var id in pendingIds) if (id.StartsWith(prefix, StringComparison.Ordinal)) return true;
                return false;
            }

            var terminal = world.Contracts.FindAll(c => c.IsTerminal);
            if (terminal.Count > SettledHistoryKept) terminal.Sort((a, b) => b.LastAccrualTick.CompareTo(a.LastAccrualTick));
            var removedPrefixes = new List<string>();
            for (int i = SettledHistoryKept; i < terminal.Count; i++)
            {
                var c = terminal[i];
                string prefix = c.Terms.ContractId + ":";
                // Unclaimed silver and debts are money, not history: keep the contract until they are settled.
                if (Pending(prefix) || c.Receivable > 0m || world.Debts.Exists(d => d.ContractId == c.Terms.ContractId)) continue;
                world.Contracts.Remove(c);
                var owned = new List<string>();
                foreach (var kv in world.Cargo)
                {
                    if (kv.Value.ContractId == c.Terms.ContractId) owned.Add(kv.Key);
                }
                foreach (var id in owned) world.Cargo.Remove(id);
                removedPrefixes.Add(prefix);
            }

            foreach (var site in world.Sites.ToArray())
            {
                if (!site.Closed || site.ClosedTick == null || now - site.ClosedTick.Value <= ClosedSiteRetention) continue;
                if (Pending(site.SiteId + ":") || Pending(site.SiteId + "-V")) continue;
                foreach (var id in site.CargoIds)
                {
                    if (world.Cargo.TryGetValue(id, out var r) && r.Custody != CargoCustody.Player) world.Cargo.Remove(id);
                }
                world.Sites.Remove(site);
                removedPrefixes.Add(site.SiteId + ":");
                removedPrefixes.Add(site.SiteId + "-V");
            }

            world.Effects.Prune(id =>
            {
                foreach (var p in removedPrefixes) if (id.StartsWith(p, StringComparison.Ordinal)) return true;
                if (id.StartsWith("claim-", StringComparison.Ordinal) || id.StartsWith("diag:", StringComparison.Ordinal))
                {
                    return world.Effects.Records.TryGetValue(id, out var r) && now - r.LastTick > FinishedEffectRetention;
                }
                return false;
            });
        }

        /// <summary>
        /// First run on a save (new game or mod added mid-game, spec 9): fixes the world seed once and stays idle until the
        /// player opens the business. Running it again — another load — changes nothing.
        /// </summary>
        public static bool Initialize(WorldState world, int worldSeed, long now)
        {
            if (world.Initialized) return false;
            world.Initialized = true;
            if (world.WorldSeed == 0) world.WorldSeed = worldSeed;
            world.InstalledTick = now;
            return true;
        }
    }

    /// <summary>Brings an older ledger to <see cref="WorldState.CurrentSchemaVersion"/>. Safe to run more than once.</summary>
    public static class Migration
    {
        public static SavedState Upgrade(SavedState saved)
        {
            if (string.IsNullOrWhiteSpace(saved.Payload) || saved.SchemaVersion <= 0) return StateCodec.Export(new WorldState());
            if (saved.SchemaVersion == WorldState.CurrentSchemaVersion) return saved;
            if (saved.SchemaVersion > WorldState.CurrentSchemaVersion)
            {
                throw new StateCodecException("RW_Diag_LedgerFromNewerVersion", "ledger schema " + saved.SchemaVersion + " is newer than " + WorldState.CurrentSchemaVersion);
            }
            // One step per version, oldest first.
            if (saved.SchemaVersion == 1) saved = FromV1(saved);
            if (saved.SchemaVersion == 2) saved = FromV2(saved);
            return saved;
        }

        /// <summary>
        /// Version 3 adds trust points, the rank table and prepayment negotiation. Trust and the table need only their
        /// defaults; an offer still open becomes negotiable, with its current rate as the opening one.
        /// </summary>
        private static SavedState FromV2(SavedState saved)
        {
            var world = StateCodec.Import(saved);
            foreach (var offer in world.Offers) offer.Terms.OpeningPrepaymentRate = offer.Terms.PrepaymentRate;
            world.SchemaVersion = 3;
            return StateCodec.Export(world);
        }

        /// <summary>
        /// Version 1 ran offers every 2 days and intel every 5 days from fixed first delays and derived the batch index
        /// from the due tick. Version 2 draws each gap, so the next index is stored; recover it from the old schedule so
        /// new contract and site IDs never repeat ones already used.
        /// </summary>
        private static SavedState FromV1(SavedState saved)
        {
            const long v1OfferInterval = 2 * CoreTime.TicksPerDay;
            const long v1DiscoveryInterval = 5 * CoreTime.TicksPerDay;
            var world = StateCodec.Import(saved);
            if (world.BusinessStartTick != null)
            {
                long start = world.BusinessStartTick.Value;
                if (world.NextOfferTick != null) world.NextOfferBatch = V1Index(world.NextOfferTick.Value - start - Limits.FirstOfferDelay, v1OfferInterval);
                if (world.NextSiteTick != null) world.NextSiteIndex = V1Index(world.NextSiteTick.Value - start - VaultSiteParameters.FirstDiscoveryDelay, v1DiscoveryInterval);
            }
            world.SchemaVersion = 2;
            return StateCodec.Export(world);
        }

        private static int V1Index(long sinceFirst, long interval) => sinceFirst <= 0 ? 0 : (int)((sinceFirst + interval - 1) / interval);
    }
}
