using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Player-visible diagnostics for ledger problems (spec 8). Logged once per key and subject.</summary>
    public static class Diagnostics
    {
        private static readonly HashSet<string> Reported = new HashSet<string>();

        public static void Report(string key, string subject)
        {
            if (!Reported.Add(key + "|" + subject)) return;
            Log.Warning("[RentalWarehouse] " + key.Translate(subject).Resolve());
            Messages.Message(key.Translate(subject), MessageTypeDefOf.CautionInput, historical: true);
        }
    }
}
