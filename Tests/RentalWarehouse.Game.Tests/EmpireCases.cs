using System;
using System.Collections.Generic;
using LudeonTK;
using RentalWarehouse.Core;
using RimWorld;
using Verse;
using static RentalWarehouse.Game.Tests.GameTestHarness;

namespace RentalWarehouse.Game.Tests
{
    /// <summary>A20/A21 parts that need the game: the Empire without Royalty and the arrival letter.</summary>
    public static class EmpireCases
    {
        [DebugAction("Rental Warehouse tests", "Empire and arrival letter cases (A20/A21)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunAll() => GameTestHarness.Run("Empire", Cases());

        private static IEnumerable<(string, Action<GameTestScope>)> Cases()
        {
            yield return ("NoLetterForAnEmptyBatch", NoLetterForAnEmptyBatch);
            yield return ("OneLetterListsTheWholeBatch", OneLetterListsTheWholeBatch);
            yield return ("EmpireDisabledWithoutRoyalty", EmpireDisabledWithoutRoyalty);
        }

        private static void NoLetterForAnEmptyBatch(GameTestScope s)
        {
            int before = Find.LetterStack.LettersListForReading.Count;

            OfferNotice.Send(s.World, new List<ContractTerms>());

            Equal(before, Find.LetterStack.LettersListForReading.Count, "letters after an empty batch");
        }

        private static void OneLetterListsTheWholeBatch(GameTestScope s)
        {
            var a = DeliveryCases.Offer(s, ContractKind.Normal, 600m);
            var b = DeliveryCases.Offer(s, ContractKind.Normal, 900m);
            int before = Find.LetterStack.LettersListForReading.Count;
            try
            {
                OfferNotice.Send(s.World, new List<ContractTerms> { a, b });

                var letters = Find.LetterStack.LettersListForReading;
                Equal(before + 1, letters.Count, "letters after a batch of two offers");
                var letter = letters[letters.Count - 1] as ChoiceLetter;
                Check(letter != null, "the offer letter is a choice letter");
                Check(letter.Text.ToString().Contains(OfferNotice.Line(a)) && letter.Text.ToString().Contains(OfferNotice.Line(b)), "the letter lists both offers");
                Find.LetterStack.RemoveLetter(letter);
            }
            finally
            {
                s.World.State.Offers.RemoveAll(o => o.Terms.ContractId == a.ContractId || o.Terms.ContractId == b.ContractId);
            }
        }

        private static void EmpireDisabledWithoutRoyalty(GameTestScope s)
        {
            if (ModsConfig.RoyaltyActive) return;
            Check(EmpireAdapter.EmpireFaction() == null, "no Empire faction without Royalty");
            Check(EmpireAdapter.Catalogue().Count == 0, "no Royalty catalogue without Royalty");
            Equal(0m, OfferGenerator.BuildContext().EmpireTitleFactor, "title factor without Royalty");
        }
    }
}
