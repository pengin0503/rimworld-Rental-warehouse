using System.Collections.Generic;
using System.Linq;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Offer list with everything spec 4 requires before acceptance.</summary>
    public static class OfferView
    {
        private const float ButtonWidth = 140f;

        public static Vector2 Draw(Rect rect, Vector2 scroll)
        {
            var world = WarehouseWorldComponent.Instance;
            var offers = world.State.Offers;
            var top = new Rect(rect.x, rect.y, rect.width, 68f);
            Widgets.Label(top, LoadSummary(world, Find.CurrentMap) + "\n" + TrustView.Summary(world.State));
            float y = top.yMax + 4f;
            var list = new Rect(rect.x, y, rect.width, rect.yMax - y);
            return WarehouseTab.ScrollList(list, scroll, offers,
                (o, w) =>
                {
                    DescribeCached(world, o);
                    return Mathf.Max(UiTextCache.Height(o, w - ButtonWidth - 12f), 76f) + 8f;
                },
                (o, r) => DrawOffer(world, o, r), "RW_NoOffers");
        }

        /// <summary>Existing load: open contracts and free/reserved slots on the current map.</summary>
        public static string LoadSummary(WarehouseWorldComponent world, Map map)
        {
            int open = ContractEngine.OpenContractCount(world.State);
            string slots = "-";
            if (map != null && map.IsPlayerHome)
            {
                var snap = Snapshot(map);
                slots = "RW_SlotsFree".Translate(snap.FreeSlots(StorageKind.Rack), snap.FreeSlots(StorageKind.Vault));
            }
            string debt = world.State.Debts.Count > 0 ? "  " + "RW_InDebtWarning".Translate() : "";
            return "RW_LoadSummary".Translate(open, Limits.MaxActiveContracts, slots) + debt;
        }

        private static string snapshotMapId;
        private static int snapshotStamp = -1;
        private static StorageSnapshot snapshot;

        /// <summary>
        /// The map's storage as of the last few ticks. The offer list asks several times per frame, and scanning every
        /// building that often is wasteful while nothing changes.
        /// </summary>
        public static StorageSnapshot Snapshot(Map map)
        {
            if (map == null || !map.IsPlayerHome) return null;
            string id = Ids.Of(map);
            int stamp = Find.TickManager.TicksGame / 15;
            if (snapshot == null || snapshotMapId != id || snapshotStamp != stamp)
            {
                snapshot = StorageAdapter.Capture(id);
                snapshotMapId = id;
                snapshotStamp = stamp;
            }
            return snapshot;
        }

        public static string FactionName(string id) => Ids.Faction(id)?.Name ?? "RW_UnknownFaction".Translate().Resolve();

        /// <summary>The offer's text as drawn every frame: rebuilt a few times a second, not on every pass.</summary>
        private static string DescribeCached(WarehouseWorldComponent world, ContractState offer) =>
            UiTextCache.Text(offer, () => Describe(world, offer.Terms, offer.Negotiation));

        public static string Describe(WarehouseWorldComponent world, ContractTerms t, NegotiationState negotiation = NegotiationState.None)
        {
            var sb = new StringBuilder();
            sb.AppendLine(("RW_Kind_" + t.Kind).Translate() + " — " + "RW_From".Translate(FactionName(t.DepositorFactionId)));
            for (int i = 0; i < t.CargoIds.Count; i++)
            {
                world.State.Cargo.TryGetValue(t.CargoIds[i], out var record);
                sb.AppendLine("  " + "RW_CrateLine".Translate(i + 1, t.CargoValues[i].ToString("0"), record?.MassKg.ToString("0.#") ?? "?", Manifest(record)));
            }
            sb.AppendLine("RW_Requires".Translate(("RW_Storage_" + t.RequiredStorage).Translate(), t.CargoIds.Count, (t.DurationTicks / (float)CoreTime.TicksPerDay).ToString("0.#")));
            sb.AppendLine(CapacityLine(world, t));
            sb.AppendLine("RW_FeeLine".Translate(Accounting.FullDailyFee(t).ToString("0.##"), (Accounting.FullTermFee(t) * t.CompletionRate).ToString("0"), t.NominalTotalReward.ToString("0")));
            if (negotiation != NegotiationState.None)
            {
                sb.AppendLine("RW_PrepaymentLineNegotiated".Translate((t.OpeningPrepaymentRate * 100m).ToString("0"), (t.PrepaymentRate * 100m).ToString("0"),
                    Accounting.Prepayment(t).ToString("0"), ("RW_Negotiation_" + negotiation).Translate()));
            }
            else
            {
                sb.AppendLine(t.PrepaymentRate > 0m
                    ? "RW_PrepaymentLine".Translate((t.PrepaymentRate * 100m).ToString("0"), Accounting.Prepayment(t).ToString("0"))
                    : "RW_NoPrepaymentLine".Translate());
            }
            sb.AppendLine("RW_CompensationLine".Translate((Limits.LossCompensationRate * 100m).ToString("0"), t.NominalTotalReward.ToString("0")));
            if (t.ScheduledRaidCount > 0)
            {
                var p = ContractParameters.For(t.Kind);
                sb.AppendLine("RW_RaidLine".Translate(t.ScheduledRaidCount, (p.RaidPointFactor * t.RaidScale).ToString("0.##"), FactionName(t.PursuerFactionId)));
                sb.AppendLine("RW_RaidRules".Translate());
            }
            else if (t.Kind == ContractKind.Normal && t.PursuerFactionId != null)
            {
                sb.AppendLine("RW_RaidNoneScheduled".Translate());
            }
            long left = t.OfferExpiresTick - Ids.Now;
            sb.Append("RW_ExpiresIn".Translate(((int)System.Math.Max(0, left)).ToStringTicksToPeriod()));
            return sb.ToString();
        }

        /// <summary>How many slots the offer needs and whether this map's racks and vaults can take it now (reservations included).</summary>
        public static string CapacityLine(WarehouseWorldComponent world, ContractTerms t)
        {
            var snap = Snapshot(Find.CurrentMap);
            if (snap == null) return "RW_CapacityUnknown".Translate(t.CargoIds.Count).ToString();
            int free = t.RequiredStorage == StorageKind.Vault ? snap.FreeSlots(StorageKind.Vault) : snap.FreeSlots(StorageKind.Rack) + snap.FreeSlots(StorageKind.Vault);
            return (ContractEngine.HasCapacity(t, snap, world.State) ? "RW_CapacityOk" : "RW_CapacityShort")
                .Translate(t.CargoIds.Count, ("RW_Storage_" + t.RequiredStorage).Translate(), free).ToString();
        }

        public static string Manifest(CargoRecord record)
        {
            if (record == null) return "?";
            return string.Join(", ", record.Manifest.Select(m =>
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(m.ThingDefName);
                return (def?.label ?? m.ThingDefName) + " x" + m.Count;
            }));
        }

        private static void DrawOffer(WarehouseWorldComponent world, ContractState offer, Rect rect)
        {
            var text = new Rect(rect.x, rect.y, rect.width - ButtonWidth - 12f, rect.height);
            Widgets.Label(text, DescribeCached(world, offer));
            var button = new Rect(rect.xMax - ButtonWidth, rect.y, ButtonWidth, 32f);
            if (Widgets.ButtonText(button, "RW_Accept".Translate()))
            {
                var terms = offer.Terms;
                var map = Find.CurrentMap;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RW_AcceptConfirm".Translate(Describe(world, terms, offer.Negotiation), map?.Parent?.LabelCap ?? "-"),
                    () =>
                    {
                        var d = StorageQuest.Accept(world, terms, map);
                        if (d.Success) Messages.Message("RW_Msg_Accepted".Translate(terms.ContractId), MessageTypeDefOf.PositiveEvent, historical: false);
                        else Messages.Message(d.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    }));
            }
            var negotiate = new Rect(button.x, button.yMax + 4f, ButtonWidth, 32f);
            if (offer.Negotiation != NegotiationState.None)
            {
                Widgets.Label(negotiate, "RW_Negotiate_Done".Translate());
            }
            else if (offer.Terms.PrepaymentRate * 100m >= NegotiationService.MaxPercentFor(TrustService.Rank(world.State)))
            {
                Widgets.Label(negotiate, "RW_Negotiate_NoRoom".Translate());
            }
            else if (Widgets.ButtonText(negotiate, "RW_Negotiate".Translate()))
            {
                Find.WindowStack.Add(new NegotiationDialog(offer));
            }
        }
    }
}
