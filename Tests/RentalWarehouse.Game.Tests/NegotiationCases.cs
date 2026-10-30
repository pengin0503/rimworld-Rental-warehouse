using System;
using System.Collections.Generic;
using LudeonTK;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>A22/A23 parts that need the game: who can negotiate, and an attempt through the live ledger.</summary>
    public static class NegotiationCases
    {
        [DebugAction("Rental Warehouse tests", "Negotiation cases (A22/A23)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Negotiation", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("CandidatesAreFreeColonistsOnHomeMaps", CandidatesAreFreeColonistsOnHomeMaps);
            yield return ("AttemptAppliesThroughTheLedgerAndOnlyOnce", AttemptAppliesThroughTheLedgerAndOnlyOnce);
        }

        private static void CandidatesAreFreeColonistsOnHomeMaps(GameTestScope s)
        {
            var candidates = NegotiatorAdapter.Candidates();

            Check(candidates.Count > 0, "at least one colonist can negotiate");
            foreach (var c in candidates)
            {
                Check(c.Pawn.IsFreeColonist && c.Pawn.Spawned && !c.Pawn.Downed, c.Pawn.LabelShort + " is a free, spawned, standing colonist");
                Check(c.Social >= 0 && c.Social <= NegotiationService.MaxSocial, c.Pawn.LabelShort + " has a social level in range");
                Check(!c.Pawn.WorkTagIsDisabled(WorkTags.Social), c.Pawn.LabelShort + " can do social work");
            }
        }

        private static void AttemptAppliesThroughTheLedgerAndOnlyOnce(GameTestScope s)
        {
            var terms = DeliveryCases.Offer(s, ContractKind.Normal, 1000m);
            try
            {
                var first = NegotiationService.Attempt(s.World.State, terms.ContractId, 5, 10, Ids.Now);
                var second = NegotiationService.Attempt(s.World.State, terms.ContractId, 6, 10, Ids.Now);

                Check(first.Success, "the first attempt is made");
                Check(first.NewState.Negotiation != NegotiationState.None, "the first attempt has an outcome");
                Check(!second.Success && second.ReasonKey == "RW_Reject_AlreadyNegotiated", "the second attempt is refused");
            }
            finally
            {
                s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == terms.ContractId);
            }
        }
    }
}
