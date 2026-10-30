using System;
using System.Collections.Generic;
using System.Text;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>The letter that announces a batch of new offers (spec 8). It stays in the letter stack until dealt with.</summary>
    public static class OfferNotice
    {
        public const string LetterDefName = "RW_OffersLetter";

        /// <summary>One letter for the whole batch; nothing for an empty batch.</summary>
        public static void Send(WarehouseWorldComponent world, IReadOnlyList<ContractTerms> created)
        {
            if (created.Count == 0) return;
            var text = new StringBuilder();
            text.AppendLine("RW_Letter_NewOffersIntro".Translate(created.Count));
            foreach (var terms in created) text.AppendLine("  " + Line(terms));
            var def = DefDatabase<LetterDef>.GetNamed(LetterDefName);
            Find.LetterStack.ReceiveLetter(LetterMaker.MakeLetter("RW_Letter_NewOffersTitle".Translate(), text.ToString().TrimEnd(), def));
        }

        public static string Line(ContractTerms t)
        {
            string depositor = OfferView.FactionName(t.DepositorFactionId);
            if (t.DepositorIsEmpire) depositor += " (" + "RW_Letter_EmpireMark".Translate() + ")";
            return "RW_Letter_OfferLine".Translate(
                depositor,
                ("RW_Kind_" + t.Kind).Translate(),
                t.CargoIds.Count,
                t.TotalValue.ToString("0"),
                (t.DurationTicks / (float)CoreTime.TicksPerDay).ToString("0.#"),
                (t.PrepaymentRate * 100m).ToString("0"),
                ((int)Math.Max(0, t.OfferExpiresTick - Ids.Now)).ToStringTicksToPeriod()).ToString();
        }
    }
}
