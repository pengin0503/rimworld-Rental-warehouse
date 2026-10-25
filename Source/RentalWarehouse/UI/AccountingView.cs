using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Claimable balance, debts and recent settlements. Keeps daily fee, balance and debt visibly separate (A14).</summary>
    public static class AccountingView
    {
        private const int HistoryShown = 10;

        public static Vector2 Draw(Rect rect, Vector2 scroll)
        {
            var world = WarehouseWorldComponent.Instance;
            var state = world.State;
            var map = Find.CurrentMap;
            string mapId = Ids.Of(map);

            float height = 600f + state.Debts.Count * 40f;
            var view = new Rect(0f, 0f, rect.width - 16f, height);
            Widgets.BeginScrollView(rect, ref scroll, view);
            var listing = new Listing_Standard();
            listing.Begin(view);

            Text.Font = GameFont.Medium;
            listing.Label("RW_Balance".Translate());
            Text.Font = GameFont.Small;
            listing.Label("RW_BalanceTotal".Translate(state.ReceivableBalance().ToString("0")));
            if (map != null && map.IsPlayerHome)
            {
                decimal here = state.ClaimableBalance(mapId);
                listing.Label("RW_BalanceHere".Translate(here.ToString("0"), map.Parent.LabelCap));
                decimal fromLostMaps = here - state.ReceivableBalance(mapId);
                if (fromLostMaps > 0m) listing.Label("RW_BalanceFromLostMaps".Translate(fromLostMaps.ToString("0")));
                if (listing.ButtonText("RW_Claim".Translate()))
                {
                    var d = PaymentService.Claim(state, mapId, PaymentAdapter.DefaultDestination(map), Ids.Now);
                    if (!d.Success) Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    else world.Enqueue(d.Effects);
                }
            }
            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("RW_Debts".Translate());
            Text.Font = GameFont.Small;
            if (state.Debts.Count == 0) listing.Label("RW_NoDebts".Translate());
            foreach (var debt in state.Debts.ToList())
            {
                listing.Label("RW_DebtLine".Translate(debt.ContractId, OfferView.FactionName(debt.FactionId), debt.Amount.ToString("0")));
                var row = listing.GetRect(32f);
                int silver = map != null && map.IsPlayerHome ? PaymentAdapter.SpendableSilverCount(map) : 0;
                bool canPay = silver >= debt.Amount;
                if (Widgets.ButtonText(row.LeftHalf().ContractedBy(2f), "RW_PayDebt".Translate(debt.Amount.ToString("0"), silver), active: canPay) && canPay)
                {
                    var d = PaymentService.ResolveDebt(state, debt.DebtId, refuse: false, Ids.Now, mapId);
                    if (d.Success) world.Enqueue(d.Effects);
                }
                if (Widgets.ButtonText(row.RightHalf().ContractedBy(2f), "RW_RefuseDebt".Translate()))
                {
                    var id = debt.DebtId;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "RW_RefuseDebtConfirm".Translate(OfferView.FactionName(debt.FactionId), -Limits.RefuseGoodwill, Limits.RefuseBlockTicks / CoreTime.TicksPerDay),
                        () =>
                        {
                            var d = PaymentService.ResolveDebt(state, id, refuse: true, Ids.Now);
                            if (d.Success) world.Enqueue(d.Effects);
                        }, destructive: true));
                }
            }
            listing.GapLine();

            Text.Font = GameFont.Medium;
            listing.Label("RW_History".Translate());
            Text.Font = GameFont.Small;
            // Cancelled contracts have no settlement but still belong in the history, with why they ended (spec 8).
            foreach (var c in state.Contracts.Where(c => c.IsTerminal).Reverse().Take(HistoryShown))
            {
                listing.Label(c.Settlement != null ? Settlement(c) : "RW_HistoryCancelled".Translate(c.Terms.ContractId, CancelReason(c)).ToString());
            }
            listing.End();
            Widgets.EndScrollView();
            return scroll;
        }

        public static string Settlement(ContractState c)
        {
            var s = c.Settlement;
            var sb = new StringBuilder();
            sb.Append(c.Terms.ContractId + " — " + ("RW_Cause_" + c.SettlementCause).Translate() + ": ");
            sb.Append("RW_SettlementLine".Translate(s.AccruedFee.ToString("0.#"), s.CompletionBonus.ToString("0.#"), s.CancellationFee.ToString("0.#"),
                s.Compensation.ToString("0.#"), s.Credit.ToString("0"), s.Debt.ToString("0"), s.GoodwillDelta.ToString("+0;-0;0")));
            if (c.Phase == ContractPhase.SystemCancelled) sb.Append(" " + CancelReason(c));
            return sb.ToString();
        }

        public static string CancelReason(ContractState c)
        {
            string key = c.CancelReason;
            if (key.NullOrEmpty()) return "";
            // Earlier versions stored the diagnostic key of system cancellations; show the matching short reason.
            if (key.StartsWith("RW_Diag_")) key = "RW_Cancel_" + key.Substring("RW_Diag_".Length);
            return key.Translate();
        }
    }
}
