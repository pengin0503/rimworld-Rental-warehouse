using System.Collections.Generic;

namespace RentalWarehouse.Core
{
    /// <summary>Claimable balance and debts (spec 5).</summary>
    public static class PaymentService
    {
        /// <summary>
        /// Turns this map's claimable balance, plus that of maps that no longer exist, into silver at
        /// <paramref name="destinationId"/>. An invalid destination keeps the balance untouched so the player can pick another (spec 5).
        /// </summary>
        public static Decision<WorldState> Claim(WorldState world, string mapId, string destinationId, long now)
        {
            if (string.IsNullOrEmpty(destinationId)) return Decision<WorldState>.Fail("RW_Reject_InvalidDestination", world);
            decimal total = world.ClaimableBalance(mapId);
            if (total <= 0m) return Decision<WorldState>.Fail("RW_Reject_NothingToClaim", world);

            foreach (var c in world.Contracts)
            {
                if (!world.ClaimableAt(c, mapId) || c.Receivable <= 0m) continue;
                c.ClaimedFee += c.Receivable;
                c.Receivable = 0m;
            }
            var effect = new EffectRequest
            {
                EffectId = world.NextSequenceId("claim"),
                Kind = EffectKind.SpawnSilver,
                EntityId = mapId,
                Amount = total,
                Payload = { ["destination"] = destinationId },
            };
            return Decision<WorldState>.Ok(world, new List<EffectRequest> { effect });
        }

        /// <summary>Called only after the entire physical payment has been reserved from the colony.</summary>
        public static void CompletePayment(WorldState world, EffectRequest effect)
        {
            if (effect.Payload.TryGetValue("debt", out var id)) world.Debts.RemoveAll(d => d.DebtId == id);
        }

        /// <summary>Release the payment lock only after its physical reservation has been refunded.</summary>
        public static void ReopenPayment(WorldState world, EffectRequest effect)
        {
            if (!effect.Payload.TryGetValue("debt", out var id)) return;
            var debt = world.Debts.Find(d => d.DebtId == id);
            if (debt != null) debt.PaymentPending = false;
        }

        /// <summary>Records settlement debts once each (spec 5: unpaid compensation becomes a ledger debt).</summary>
        public static void SyncDebts(WorldState world, long now)
        {
            foreach (var c in world.Contracts)
            {
                if (c.DebtRecorded || c.Settlement == null || c.Settlement.Debt <= 0m) continue;
                world.Debts.Add(new Debt
                {
                    DebtId = "Debt-" + c.Terms.ContractId,
                    ContractId = c.Terms.ContractId,
                    FactionId = c.Terms.DepositorFactionId,
                    Amount = c.Settlement.Debt,
                    CreatedTick = now,
                });
                c.DebtRecorded = true;
            }
        }

        /// <summary>
        /// Pays a debt with silver from <paramref name="mapId"/>, or refuses it: goodwill −20 and that faction offers no
        /// contracts for 15 days. Existing contracts continue either way.
        /// </summary>
        public static Decision<WorldState> ResolveDebt(WorldState world, string debtId, bool refuse, long now, string? mapId = null)
        {
            var debt = world.Debts.Find(d => d.DebtId == debtId);
            if (debt == null) return Decision<WorldState>.Fail("RW_Reject_UnknownDebt", world);
            if (!refuse && string.IsNullOrEmpty(mapId)) return Decision<WorldState>.Fail("RW_Reject_InvalidDestination", world);

            if (debt.PaymentPending) return Decision<WorldState>.Fail("RW_Reject_PaymentPending", world);
            if (refuse) world.Debts.Remove(debt);
            else debt.PaymentPending = true;
            var effects = new List<EffectRequest>();
            if (refuse)
            {
                world.FactionBlockedUntil[debt.FactionId] = now + Limits.RefuseBlockTicks;
                effects.Add(new EffectRequest
                {
                    EffectId = debtId + ":refuse",
                    Kind = EffectKind.ApplyGoodwill,
                    EntityId = debt.FactionId,
                    Amount = Limits.RefuseGoodwill,
                    Payload = { ["debt"] = debtId },
                });
            }
            else
            {
                effects.Add(new EffectRequest
                {
                    EffectId = world.NextSequenceId("debt-payment"),
                    Kind = EffectKind.ConsumeSilver,
                    EntityId = mapId!,
                    Amount = debt.Amount,
                    Payload = { ["debt"] = debtId },
                });
            }
            return Decision<WorldState>.Ok(world, effects);
        }
    }
}
