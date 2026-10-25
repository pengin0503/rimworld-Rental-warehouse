using System;
using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    public static class ContractTermsFactory
    {
        public static ContractTerms Create(ContractParameters p, string contractId, string depositorFactionId, string? pursuerFactionId,
            IList<(string CargoId, decimal Value)> cargo, int scheduledRaids, WarehouseSettings settings, int settingsVersion, int seed, long now)
        {
            var terms = new ContractTerms
            {
                ContractId = contractId,
                DepositorFactionId = depositorFactionId,
                PursuerFactionId = pursuerFactionId,
                Kind = p.Kind,
                RequiredStorage = p.RequiredStorage,
                DurationTicks = p.DurationTicks,
                DailyBase = p.DailyBase,
                DailyValueRate = p.DailyValueRate,
                CompletionRate = p.CompletionRate,
                FeeMultiplier = settings.FeeMultiplier,
                RaidScale = settings.RaidScale,
                ScheduledRaidCount = pursuerFactionId == null ? 0 : Math.Max(0, scheduledRaids),
                Seed = seed,
                SettingsVersion = settingsVersion,
                OfferedTick = now,
                OfferExpiresTick = now + Limits.OfferLifetime,
            };
            foreach (var (id, value) in cargo)
            {
                terms.CargoIds.Add(id);
                terms.CargoValues.Add(value);
            }
            terms.NominalTotalReward = Accounting.NominalTotalReward(terms);
            return terms;
        }
    }

    /// <summary>Contract state machine (spec 4/5): Offered → AwaitingDelivery → AwaitingStorage → Active → Returning → Settled.</summary>
    public static class ContractEngine
    {
        public static Decision<WorldState> Accept(ContractTerms terms, StorageSnapshot storage, WorldState world, long now)
        {
            var offer = world.Offers.Find(o => o.Terms.ContractId == terms.ContractId);
            if (offer == null || world.Contract(terms.ContractId) != null) return Decision<WorldState>.Fail("RW_Reject_NotOffered", world);
            if (!ValidTerms(offer.Terms, world)) return Decision<WorldState>.Fail("RW_Reject_InvalidTerms", world);
            if (now >= offer.Terms.OfferExpiresTick) return Decision<WorldState>.Fail("RW_Reject_OfferExpired", world);
            if (world.Debts.Count > 0) return Decision<WorldState>.Fail("RW_Reject_InDebt", world);
            if (world.FactionBlockedUntil.TryGetValue(offer.Terms.DepositorFactionId, out long until) && now < until)
            {
                return Decision<WorldState>.Fail("RW_Reject_FactionBlocked", world);
            }
            if (OpenContractCount(world) >= Limits.MaxActiveContracts) return Decision<WorldState>.Fail("RW_Reject_TooManyContracts", world);
            if (!HasCapacity(offer.Terms, storage, world)) return Decision<WorldState>.Fail("RW_Reject_NoCapacity", world);

            var state = new ContractState
            {
                Terms = offer.Terms,
                Phase = ContractPhase.AwaitingDelivery,
                MapId = storage.MapId,
                AcceptedTick = now,
                DeliveryTick = now + Limits.DeliveryDelay,
                LastAccrualTick = now,
            };
            for (int i = 0; i < offer.Terms.CargoIds.Count; i++)
            {
                state.CargoStates.Add(new ContractCargoState { CargoId = offer.Terms.CargoIds[i], DeclaredValue = offer.Terms.CargoValues[i] });
            }
            world.Offers.Remove(offer);
            world.Contracts.Add(state);
            return Decision<WorldState>.Ok(world);
        }

        public static int OpenContractCount(WorldState world)
        {
            int open = 0;
            foreach (var c in world.Contracts)
            {
                if (!c.IsTerminal) open++;
            }
            return open;
        }

        private static bool ValidTerms(ContractTerms t, WorldState world)
        {
            if (t.CargoIds.Count < Limits.MinCrates || t.CargoIds.Count > Limits.MaxCrates) return false;
            if (t.CargoIds.Count != t.CargoValues.Count) return false;
            if (t.DurationTicks <= 0 || t.DailyBase < 0m || t.DailyValueRate < 0m || t.CompletionRate < 0m || t.FeeMultiplier <= 0m) return false;
            if (string.IsNullOrEmpty(t.ContractId) || string.IsNullOrEmpty(t.DepositorFactionId)) return false;
            var seen = new HashSet<string>();
            for (int i = 0; i < t.CargoIds.Count; i++)
            {
                if (t.CargoValues[i] <= 0m || !seen.Add(t.CargoIds[i])) return false;
                if (!world.Cargo.TryGetValue(t.CargoIds[i], out var record)) return false;
                if (record.MassKg <= 0m || record.MassKg > Limits.MaxCrateMassKg) return false;
            }
            return true;
        }

        /// <summary>
        /// Vault cargo needs vault slots; rack cargo may use either. Slots already promised to accepted but
        /// not yet stored contracts on the same map are reserved.
        /// </summary>
        private static bool HasCapacity(ContractTerms terms, StorageSnapshot storage, WorldState world)
        {
            int reservedVault = 0, reservedAll = 0;
            foreach (var c in world.Contracts)
            {
                if (c.MapId != storage.MapId) continue;
                if (c.Phase != ContractPhase.AwaitingDelivery && c.Phase != ContractPhase.AwaitingStorage) continue;
                foreach (var cs in c.CargoStates)
                {
                    if (cs.Loss != LossCause.None || storage.ValidStoredCargoIds.Contains(cs.CargoId)) continue;
                    reservedAll++;
                    if (c.Terms.RequiredStorage == StorageKind.Vault) reservedVault++;
                }
            }
            int vaultFree = storage.FreeSlots(StorageKind.Vault);
            int allFree = vaultFree + storage.FreeSlots(StorageKind.Rack);
            int need = terms.CargoIds.Count;
            int needVault = terms.RequiredStorage == StorageKind.Vault ? need : 0;
            return reservedVault + needVault <= vaultFree && reservedAll + need <= allFree;
        }

        public static Decision<ContractState> Advance(ContractState state, StorageSnapshot storage, long now)
        {
            var effects = new List<EffectRequest>();
            if (state.IsTerminal || now < state.LastAccrualTick) return Decision<ContractState>.Ok(state, effects);

            switch (state.Phase)
            {
                case ContractPhase.AwaitingDelivery:
                    if (now >= state.DeliveryTick) EmitDeliveries(state, effects);
                    break;

                case ContractPhase.AwaitingStorage:
                    if (AllRemainingStored(state, storage))
                    {
                        state.Phase = ContractPhase.Active;
                        state.ActiveTick = now;
                        state.EndTick = now + state.Terms.DurationTicks;
                    }
                    else if (now >= state.DeliveredTick.GetValueOrDefault(now) + Limits.StorageDeadline)
                    {
                        BeginReturn(state, ReturnReason.StorageFailed, now);
                    }
                    break;

                case ContractPhase.Active:
                    Progress(state, now, effects);
                    break;

                case ContractPhase.Returning:
                    if (now >= state.ReturnDeadlineTick.GetValueOrDefault(long.MaxValue))
                    {
                        foreach (var cs in state.CargoStates)
                        {
                            if (cs.Loss != LossCause.None || cs.Returned) continue;
                            cs.Loss = LossCause.Lost;
                            cs.LossTick = now;
                            effects.Add(RemoveCargo(state, cs.CargoId, "unreturned"));
                        }
                        Finish(state, now, effects);
                    }
                    break;
            }

            if (state.Phase == ContractPhase.Active || state.Phase == ContractPhase.AwaitingStorage)
            {
                foreach (var cs in state.CargoStates)
                {
                    bool stored = storage.ValidStoredCargoIds.Contains(cs.CargoId);
                    if (state.Phase == ContractPhase.Active && cs.ValidlyStored && !stored && cs.Loss == LossCause.None && !cs.Returned)
                    {
                        // Spec 8: tell the player once per crate; the effect ID makes repeats no-ops.
                        effects.Add(Notice(state, "feestop:" + cs.CargoId, "RW_Msg_FeeStopped"));
                    }
                    cs.ValidlyStored = stored;
                }
            }
            state.LastAccrualTick = Math.Max(state.LastAccrualTick, now);
            return Decision<ContractState>.Ok(state, effects);
        }

        /// <summary>Accrues to <paramref name="now"/> (never past maturity), posting the day-2/day-4 payments and maturing on the way.</summary>
        private static void Progress(ContractState state, long now, List<EffectRequest> effects)
        {
            long active = state.ActiveTick!.Value;
            long end = state.EndTick!.Value;
            while (state.Phase == ContractPhase.Active)
            {
                long boundary = state.PaymentsPosted < Limits.PaymentDays.Length
                    ? Math.Min(active + Limits.PaymentDays[state.PaymentsPosted], end)
                    : end;
                if (boundary > now)
                {
                    AccrueTo(state, now);
                    return;
                }
                AccrueTo(state, boundary);
                if (boundary == end)
                {
                    state.PaymentsPosted = Limits.PaymentDays.Length;
                    BeginReturn(state, ReturnReason.Matured, end);
                    effects.Add(Notice(state, "matured", "RW_Msg_Matured"));
                }
                else
                {
                    PostPayment(state, effects);
                }
            }
        }

        private static void AccrueTo(ContractState state, long t)
        {
            long ticks = t - state.LastAccrualTick;
            if (ticks <= 0) return;
            decimal total = 0m;
            foreach (var cs in state.CargoStates)
            {
                if (cs.ValidlyStored && cs.Loss == LossCause.None && !cs.Returned) cs.StoredTicks += ticks;
                cs.AccruedFee = Accounting.FeeForTicks(state.Terms, cs.DeclaredValue, cs.StoredTicks);
                total += cs.AccruedFee;
            }
            state.AccruedFee = total;
            state.LastAccrualTick = t;
        }

        private static void PostPayment(ContractState state, List<EffectRequest> effects)
        {
            state.PaymentsPosted++;
            decimal amount = decimal.Floor(state.AccruedFee) - state.PostedFee;
            if (amount <= 0m) return;
            state.PostedFee += amount;
            state.Receivable += amount;
            effects.Add(new EffectRequest
            {
                EffectId = Id(state, "pay", state.PaymentsPosted.ToString(CultureInfo.InvariantCulture)),
                Kind = EffectKind.Notify,
                EntityId = state.Terms.ContractId,
                Amount = amount,
                Payload = { ["key"] = "RW_Msg_PaymentPosted" },
            });
        }

        private static void EmitDeliveries(ContractState state, List<EffectRequest> effects)
        {
            foreach (var cs in state.CargoStates)
            {
                if (cs.Delivered || cs.Loss != LossCause.None) continue;
                effects.Add(new EffectRequest
                {
                    EffectId = Id(state, "deliver", cs.CargoId),
                    Kind = EffectKind.DeliverCargo,
                    EntityId = cs.CargoId,
                    Payload = { ["contract"] = state.Terms.ContractId, ["map"] = state.MapId },
                });
            }
        }

        private static bool AllRemainingStored(ContractState state, StorageSnapshot storage)
        {
            bool any = false;
            foreach (var cs in state.CargoStates)
            {
                if (cs.Loss != LossCause.None) continue;
                any = true;
                if (!cs.Delivered || !storage.ValidStoredCargoIds.Contains(cs.CargoId)) return false;
            }
            return any;
        }

        public static Decision<ContractState> MarkDelivered(ContractState state, string cargoId, long now)
        {
            var cs = state.Cargo(cargoId);
            if (cs == null) return Decision<ContractState>.Fail("RW_Reject_UnknownCargo", state);
            if (cs.Delivered || state.IsTerminal) return Decision<ContractState>.Fail("RW_Reject_AlreadyDelivered", state);
            cs.Delivered = true;
            if (state.Phase == ContractPhase.AwaitingDelivery && state.CargoStates.TrueForAll(c => c.Delivered || c.Loss != LossCause.None))
            {
                state.Phase = ContractPhase.AwaitingStorage;
                state.DeliveredTick = now;
            }
            return Decision<ContractState>.Ok(state);
        }

        public static Decision<ContractState> MarkLost(ContractState state, string cargoId, LossCause cause, long now)
        {
            var cs = state.Cargo(cargoId);
            if (cs == null) return Decision<ContractState>.Fail("RW_Reject_UnknownCargo", state);
            if (state.IsTerminal || cs.Loss != LossCause.None || cs.Returned || cause == LossCause.None)
            {
                return Decision<ContractState>.Fail("RW_Reject_CargoNotHeld", state);
            }

            var effects = new List<EffectRequest>();
            if (state.Phase == ContractPhase.Active) Progress(state, now, effects);
            cs.Loss = cause;
            cs.LossTick = now;
            cs.ValidlyStored = false;
            if (cause == LossCause.Lost) effects.Add(Notice(state, "lost:" + cargoId, "RW_Msg_CrateLost"));

            if (state.CargoStates.TrueForAll(c => c.Loss != LossCause.None))
            {
                Finish(state, now, effects);
            }
            else if (state.Phase == ContractPhase.AwaitingDelivery || state.Phase == ContractPhase.AwaitingStorage)
            {
                BeginReturn(state, ReturnReason.StorageFailed, now);
            }
            else if (state.Phase == ContractPhase.Returning && NothingLeftToReturn(state))
            {
                Finish(state, now, effects);
            }
            return Decision<ContractState>.Ok(state, effects);
        }

        public static Decision<ContractState> MarkReturned(ContractState state, string cargoId, long now)
        {
            var cs = state.Cargo(cargoId);
            if (cs == null) return Decision<ContractState>.Fail("RW_Reject_UnknownCargo", state);
            if (state.Phase != ContractPhase.Returning) return Decision<ContractState>.Fail("RW_Reject_NotReturning", state);
            if (cs.Returned || cs.Loss != LossCause.None) return Decision<ContractState>.Fail("RW_Reject_CargoNotHeld", state);
            cs.Returned = true;
            cs.ValidlyStored = false;
            var effects = new List<EffectRequest>();
            if (NothingLeftToReturn(state)) Finish(state, now, effects);
            return Decision<ContractState>.Ok(state, effects);
        }

        public static Decision<ContractState> RequestEarlyReturn(ContractState state, ReturnReason reason, long now)
        {
            if (state.Phase != ContractPhase.Active && state.Phase != ContractPhase.AwaitingStorage)
            {
                return Decision<ContractState>.Fail("RW_Reject_NotReturnable", state);
            }
            var effects = new List<EffectRequest>();
            if (state.Phase == ContractPhase.Active) Progress(state, now, effects);
            if (state.Phase == ContractPhase.Returning) return Decision<ContractState>.Ok(state, effects);
            BeginReturn(state, reason, now);
            return Decision<ContractState>.Ok(state, effects);
        }

        public const string CancelByPlayer = "RW_Cancel_ByPlayer";

        /// <summary>Penalty-free cancellation before any crate arrives; <paramref name="reasonKey"/> records who or what ended it.</summary>
        public static Decision<ContractState> Cancel(ContractState state, long now, string reasonKey = CancelByPlayer)
        {
            if (state.Phase != ContractPhase.AwaitingDelivery) return Decision<ContractState>.Fail(
                state.IsTerminal ? "RW_Reject_NotCancellable" : "RW_Reject_AlreadyArrived", state);
            if (state.CargoStates.Exists(c => c.Delivered)) return Decision<ContractState>.Fail("RW_Reject_AlreadyArrived", state);
            state.Phase = ContractPhase.Cancelled;
            state.CancelReason = reasonKey;
            state.LastAccrualTick = Math.Max(state.LastAccrualTick, now);
            return Decision<ContractState>.Ok(state);
        }

        /// <summary>Ends a contract without the player's fault (spec 8). Accrued fees stay; no completion, compensation or goodwill.</summary>
        public static Decision<ContractState> SystemCancel(ContractState state, string reasonKey, long now)
        {
            if (state.IsTerminal) return Decision<ContractState>.Fail("RW_Reject_NotCancellable", state);
            var effects = new List<EffectRequest>();
            if (state.Phase == ContractPhase.Active) AccrueTo(state, Math.Min(now, state.EndTick ?? now));
            if (state.Phase == ContractPhase.Offered || state.Phase == ContractPhase.AwaitingDelivery && !state.CargoStates.Exists(c => c.Delivered))
            {
                state.Phase = ContractPhase.SystemCancelled;
                state.CancelReason = reasonKey;
                return Decision<ContractState>.Ok(state, effects);
            }
            foreach (var cs in state.CargoStates)
            {
                if (cs.Loss != LossCause.None || cs.Returned) continue;
                cs.Loss = LossCause.SystemRemoved;
                cs.LossTick = now;
                if (cs.Delivered) effects.Add(RemoveCargo(state, cs.CargoId, "system"));
            }
            state.CancelReason = reasonKey;
            state.SettlementCause = SettlementCause.SystemCancelled;
            Finish(state, now, effects);
            state.Phase = ContractPhase.SystemCancelled;
            return Decision<ContractState>.Ok(state, effects);
        }

        private static bool NothingLeftToReturn(ContractState state) =>
            state.CargoStates.TrueForAll(c => c.Returned || c.Loss != LossCause.None);

        private static void BeginReturn(ContractState state, ReturnReason reason, long now)
        {
            state.Phase = ContractPhase.Returning;
            state.ReturnReason = reason;
            long window = reason == ReturnReason.DepositorHostile ? Limits.HostileReturnWindow : Limits.ReturnGrace;
            state.ReturnDeadlineTick = now + window;
        }

        private static void Finish(ContractState state, long now, List<EffectRequest> effects)
        {
            if (state.SettlementCause != SettlementCause.SystemCancelled) state.SettlementCause = Cause(state);
            var result = Accounting.Settle(state);
            state.Settlement = result;
            state.Receivable = result.Credit;
            state.Phase = ContractPhase.Settled;
            state.LastAccrualTick = Math.Max(state.LastAccrualTick, now);
            if (result.GoodwillDelta != 0)
            {
                effects.Add(new EffectRequest
                {
                    EffectId = Id(state, "settle", "goodwill"),
                    Kind = EffectKind.ApplyGoodwill,
                    EntityId = state.Terms.DepositorFactionId,
                    Amount = result.GoodwillDelta,
                    Payload = { ["contract"] = state.Terms.ContractId },
                });
            }
            effects.Add(new EffectRequest
            {
                EffectId = Id(state, "settle", "notify"),
                Kind = EffectKind.Notify,
                EntityId = state.Terms.ContractId,
                Amount = result.Credit - result.Debt,
                Payload = { ["key"] = "RW_Msg_Settled", ["cause"] = state.SettlementCause.ToString() },
            });
        }

        private static SettlementCause Cause(ContractState state)
        {
            bool anyReturned = state.CargoStates.Exists(c => c.Returned);
            bool anyLoss = state.CargoStates.Exists(c => c.Loss == LossCause.Lost || c.Loss == LossCause.Embezzled);
            if (!anyReturned && anyLoss && state.CargoStates.TrueForAll(c => c.Loss != LossCause.None)) return SettlementCause.TotalLoss;
            switch (state.ReturnReason)
            {
                case ReturnReason.EarlyByPlayer: return SettlementCause.EarlyReturn;
                case ReturnReason.StorageFailed: return SettlementCause.StorageFailed;
                case ReturnReason.DepositorHostile: return SettlementCause.DepositorHostile;
            }
            return anyLoss ? SettlementCause.PartialLoss : SettlementCause.Completed;
        }

        internal static EffectRequest RemoveCargo(ContractState state, string cargoId, string why) => new EffectRequest
        {
            EffectId = Id(state, "remove", cargoId),
            Kind = EffectKind.RemoveCargo,
            EntityId = cargoId,
            Payload = { ["contract"] = state.Terms.ContractId, ["reason"] = why },
        };

        private static EffectRequest Notice(ContractState state, string what, string key) => new EffectRequest
        {
            EffectId = state.Terms.ContractId + ":" + what,
            Kind = EffectKind.Notify,
            EntityId = state.Terms.ContractId,
            Payload = { ["key"] = key },
        };

        internal static string Id(ContractState state, string what, string detail) => state.Terms.ContractId + ":" + what + ":" + detail;
    }
}
