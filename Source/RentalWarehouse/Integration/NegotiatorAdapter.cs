using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>A colonist who can negotiate a prepayment, with the social skill level the negotiation uses.</summary>
    public sealed class Negotiator
    {
        public Pawn Pawn;
        public int Social;
    }

    /// <summary>Who may negotiate (spec 6.4): free colonists standing on a home map who can talk and do social work.</summary>
    public static class NegotiatorAdapter
    {
        public static List<Negotiator> Candidates()
        {
            var list = new List<Negotiator>();
            foreach (var pawn in PawnsFinder.HomeMaps_FreeColonistsSpawned)
            {
                if (pawn.Downed || pawn.skills == null || pawn.WorkTagIsDisabled(WorkTags.Social)) continue;
                if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Talking)) continue;
                list.Add(new Negotiator { Pawn = pawn, Social = pawn.skills.GetSkill(SkillDefOf.Social).Level });
            }
            return list;
        }
    }
}
