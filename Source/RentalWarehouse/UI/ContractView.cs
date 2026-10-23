using System.Collections.Generic;
using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Accepted contracts: phase, deadlines, per-crate state and fee-stop reasons, and the player's actions.</summary>
    public static class ContractView
    {
        private const float ButtonWidth = 170f;

        public static Vector2 Draw(Rect rect, Vector2 scroll)
        {
            var world = WarehouseWorldComponent.Instance;
            var list = world.State.Contracts.Where(c => !c.IsTerminal).ToList();
            return WarehouseTab.ScrollList(rect, scroll, list,
                (c, w) => Mathf.Max(WarehouseTab.TextHeight(Describe(world, c), w - ButtonWidth - 12f), 3 * 36f) + 8f,
                (c, r) => DrawContract(world, c, r), "RW_NoContracts");
        }

        private static string Days(long ticks) => ((int)System.Math.Max(0, ticks)).ToStringTicksToPeriod();

        public static string Describe(WarehouseWorldComponent world, ContractState c)
        {
            var t = c.Terms;
            long now = Ids.Now;
            var sb = new StringBuilder();
            sb.AppendLine(t.ContractId + " — " + ("RW_Kind_" + t.Kind).Translate() + " — " + OfferView.FactionName(t.DepositorFactionId));
            sb.AppendLine(("RW_Phase_" + c.Phase).Translate());
            switch (c.Phase)
            {
                case ContractPhase.AwaitingDelivery:
                    sb.AppendLine("RW_ArrivesIn".Translate(Days(c.DeliveryTick - now)));
                    break;
                case ContractPhase.AwaitingStorage:
                    sb.AppendLine("RW_StoreWithin".Translate(Days(c.DeliveredTick.GetValueOrDefault(now) + Limits.StorageDeadline - now)));
                    break;
                case ContractPhase.Active:
                    sb.AppendLine("RW_TermEndsIn".Translate(Days(c.EndTick.GetValueOrDefault(now) - now)));
                    break;
                case ContractPhase.Returning:
                    sb.AppendLine("RW_ReturnDeadline".Translate(Days(c.ReturnDeadlineTick.GetValueOrDefault(now) - now)) + " " + ("RW_ReturnReason_" + c.ReturnReason).Translate());
                    break;
            }
            sb.AppendLine("RW_ContractMoney".Translate(Accounting.FullDailyFee(t).ToString("0.##"), c.AccruedFee.ToString("0.#"), c.Receivable.ToString("0"), c.ClaimedFee.ToString("0")));
            if (t.ScheduledRaidCount > 0)
            {
                sb.AppendLine("RW_RaidProgress".Translate(c.StartedRaids, t.ScheduledRaidCount));
            }
            for (int i = 0; i < c.CargoStates.Count; i++)
            {
                sb.AppendLine("  " + "RW_CrateStatus".Translate(i + 1, c.CargoStates[i].DeclaredValue.ToString("0"), CrateStatus(world, c, c.CargoStates[i])));
            }
            return sb.ToString().TrimEnd();
        }

        private static string CrateStatus(WarehouseWorldComponent world, ContractState c, ContractCargoState cs)
        {
            if (cs.Returned) return "RW_CrateReturned".Translate();
            if (cs.Loss != LossCause.None) return ("RW_Loss_" + cs.Loss).Translate();
            if (!cs.Delivered) return "RW_CrateInTransit".Translate();
            var crate = world.FindCrate(cs.CargoId);
            world.State.Cargo.TryGetValue(cs.CargoId, out var record);
            if (crate == null || record == null) return "RW_CrateMissing".Translate();
            var carrier = ThreatAdapter.Carrier(crate);
            if (carrier != null && carrier.HostileTo(Faction.OfPlayer)) return "RW_CrateBeingStolen".Translate(carrier.LabelShort);
            string reason = StorageAdapter.FeeStopReason(crate, record);
            string fee = cs.AccruedFee.ToString("0.#");
            return reason == null ? "RW_CrateEarning".Translate(fee) : "RW_CrateStopped".Translate(reason.Translate(), fee);
        }

        private static void DrawContract(WarehouseWorldComponent world, ContractState c, Rect rect)
        {
            Widgets.Label(new Rect(rect.x, rect.y, rect.width - ButtonWidth - 12f, rect.height), Describe(world, c));
            float y = rect.y;
            Rect Next()
            {
                var r = new Rect(rect.xMax - ButtonWidth, y, ButtonWidth, 32f);
                y += 36f;
                return r;
            }

            if (c.Phase == ContractPhase.AwaitingDelivery && Widgets.ButtonText(Next(), "RW_CancelContract".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RW_CancelConfirm".Translate(c.Terms.ContractId), () =>
                {
                    var d = ContractEngine.Cancel(c, Ids.Now);
                    if (!d.Success) Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    else world.Enqueue(d.Effects);
                }));
            }
            if ((c.Phase == ContractPhase.Active || c.Phase == ContractPhase.AwaitingStorage) && Widgets.ButtonText(Next(), "RW_EarlyReturn".Translate()))
            {
                decimal fee = System.Math.Min(c.AccruedFee, Accounting.FullDailyFee(c.Terms) * Limits.CancellationFeeDays);
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RW_EarlyReturnConfirm".Translate(c.Terms.ContractId, fee.ToString("0")), () =>
                {
                    var d = ContractEngine.RequestEarlyReturn(c, ReturnReason.EarlyByPlayer, Ids.Now);
                    if (!d.Success) Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    else world.Enqueue(d.Effects);
                }, destructive: true));
            }
            if (c.Phase == ContractPhase.Returning)
            {
                var button = Next();
                if (c.ActiveReturnPodId != null)
                {
                    var pod = ReturnAdapter.FindPod(Ids.Map(c.MapId), c.ActiveReturnPodId);
                    if (Widgets.ButtonText(button, "RW_ShowReturnPod".Translate(), active: pod != null) && pod != null) CameraJumper.TryJumpAndSelect(pod);
                }
                else if (Widgets.ButtonText(button, "RW_RequestReturnPod".Translate()))
                {
                    ReturnAdapter.RequestPod(c);
                }
            }
        }
    }
}
