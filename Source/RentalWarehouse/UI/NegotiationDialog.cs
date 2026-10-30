using System;
using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace RentalWarehouse
{
    /// <summary>
    /// Asks for a higher prepayment on one offer (spec 6.5): pick the negotiator, pick the rate, see the chance, try once.
    /// The outcome is decided by <see cref="NegotiationService"/>; this window only collects the choices.
    /// </summary>
    public class NegotiationDialog : Window
    {
        private const float RowHeight = 28f;

        private readonly ContractState offer;
        private readonly List<Negotiator> candidates;
        private readonly int opening;
        private int selected;
        private int requested;
        private Vector2 scroll;

        public NegotiationDialog(ContractState offer)
        {
            this.offer = offer;
            candidates = NegotiatorAdapter.Candidates();
            candidates.Sort((a, b) => b.Social.CompareTo(a.Social));
            opening = (int)Math.Round(Math.Max(offer.Terms.OpeningPrepaymentRate, offer.Terms.PrepaymentRate) * 100m, MidpointRounding.AwayFromZero);
            requested = Math.Min(NegotiationService.MaxPercent, opening + 5);
            doCloseButton = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => new Vector2(520f, 520f);

        public override void DoWindowContents(Rect inRect)
        {
            var world = WarehouseWorldComponent.Instance;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "RW_NegotiateTitle".Translate());
            Text.Font = GameFont.Small;
            string intro = "RW_NegotiateIntro".Translate(offer.Terms.ContractId, opening);
            float introHeight = Text.CalcHeight(intro, inRect.width);
            Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, introHeight), intro);

            float y = inRect.y + 40f + introHeight;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "RW_NegotiateNegotiator".Translate());
            y += 26f;

            if (candidates.Count == 0)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 28f), "RW_NegotiateNoOne".Translate());
                return;
            }

            var listRect = new Rect(inRect.x, y, inRect.width, 150f);
            var view = new Rect(0f, 0f, listRect.width - 16f, candidates.Count * RowHeight);
            Widgets.BeginScrollView(listRect, ref scroll, view);
            for (int i = 0; i < candidates.Count; i++)
            {
                var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
                if (i == selected) Widgets.DrawHighlightSelected(row);
                Widgets.Label(row, candidates[i].Pawn.LabelShortCap + "  —  " + "RW_NegotiateSocial".Translate(candidates[i].Social));
                if (Widgets.ButtonInvisible(row)) selected = i;
            }
            Widgets.EndScrollView();
            y = listRect.yMax + 10f;

            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "RW_NegotiateAsk".Translate(requested));
            y += 26f;
            requested = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(inRect.x, y, inRect.width, 24f), requested, opening + 1, NegotiationService.MaxPercent));
            requested = Mathf.Clamp(requested, opening + 1, NegotiationService.MaxPercent);
            y += 30f;

            decimal chance = NegotiationService.SuccessChance(opening, requested, candidates[selected].Social, TrustService.Rank(world.State));
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "RW_NegotiateChance".Translate((chance * 100m).ToString("0")));
            y += 34f;

            if (Widgets.ButtonText(new Rect(inRect.x, y, 200f, 32f), "RW_NegotiateGo".Translate())) Go(world);
        }

        private void Go(WarehouseWorldComponent world)
        {
            string id = offer.Terms.ContractId;
            var result = NegotiationService.Attempt(world.State, id, requested, candidates[selected].Social, Ids.Now);
            if (!result.Success)
            {
                Messages.Message(result.ReasonKey.Translate(), MessageTypeDefOf.RejectInput, historical: false);
            }
            else if (result.NewState.Negotiation == NegotiationState.Succeeded)
            {
                Messages.Message("RW_Msg_NegotiationSucceeded".Translate(id, requested), MessageTypeDefOf.PositiveEvent, historical: false);
            }
            else
            {
                Messages.Message("RW_Msg_NegotiationFailed".Translate(id, opening), MessageTypeDefOf.NegativeEvent, historical: false);
            }
            Close();
        }
    }
}
