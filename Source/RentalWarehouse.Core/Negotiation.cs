using System;

namespace RentalWarehouse.Core
{
    /// <summary>
    /// Prepayment negotiation (spec 6). The player may ask once, per offer, for a higher prepayment rate, up to a limit that grows with the trust rank, and what is gained is paid for out of the completion rate. Whether it works is
    /// the negotiator's social skill and the trust rank weighed against how much more is asked, and the dice are the offer's
    /// own seed, so loading a save never rerolls the outcome.
    /// </summary>
    public static class NegotiationService
    {
        /// <summary>The highest rate anyone may ask for before trust lifts it.</summary>
        public const int BaseMaxPercent = Limits.MaxPrepaymentPercent;
        public const int MaxSocial = 20;

        private const decimal Base = 0.85m;
        private const decimal PerPointAsked = 0.04m;
        private const decimal PerSocialLevel = 0.025m;
        private const decimal PerRank = 0.03m;
        private const decimal Floor = 0.05m;
        private const decimal Ceiling = 0.95m;
        /// <summary>Each percentage point of prepayment asked above the opening costs this share of the completion rate.</summary>
        private const decimal CompletionCostPerPoint = 0.02m;

        public static decimal SuccessChance(int openingPercent, int requestedPercent, int socialLevel, int rank)
        {
            int social = Math.Max(0, Math.Min(MaxSocial, socialLevel));
            decimal chance = Base - PerPointAsked * (requestedPercent - openingPercent) + PerSocialLevel * social + PerRank * rank;
            return Math.Max(Floor, Math.Min(Ceiling, chance));
        }

        /// <summary>The highest prepayment rate (percent) that may be asked for at a trust rank.</summary>
        public static int MaxPercentFor(int rank) =>
            Math.Min(Limits.MaxNegotiatedPercent, BaseMaxPercent + Limits.PrepaymentPercentPerRank * Math.Max(0, rank));

        /// <summary>The completion rate left after asking <paramref name="pointsAsked"/> percentage points more prepayment.</summary>
        public static decimal CompletionRateAfter(decimal completionRate, int pointsAsked) =>
            completionRate * (1m - CompletionCostPerPoint * Math.Max(0, pointsAsked));

        /// <summary>The offer's fixed roll in [0, 1): the negotiation works when it is below the success chance.</summary>
        public static decimal Roll(int offerSeed) =>
            new DeterministicRandom(DeterministicRandom.Mix(offerSeed, "negotiate")).NextDecimal();

        /// <summary>
        /// Makes the one attempt. <c>Success</c> means the attempt was made; the outcome is the returned offer's
        /// <see cref="ContractState.Negotiation"/>. A refused attempt changes nothing (its state is the offer, or an empty
        /// one when no such offer exists).
        /// </summary>
        public static Decision<ContractState> Attempt(WorldState world, string contractId, int requestedPercent, int socialLevel, long now)
        {
            var offer = world.Offers.Find(o => o.Terms.ContractId == contractId && o.Phase == ContractPhase.Offered);
            if (offer == null) return Decision<ContractState>.Fail("RW_Reject_NotOffered", new ContractState());
            var terms = offer.Terms;
            if (now >= terms.OfferExpiresTick) return Decision<ContractState>.Fail("RW_Reject_OfferExpired", offer);
            if (offer.Negotiation != NegotiationState.None) return Decision<ContractState>.Fail("RW_Reject_AlreadyNegotiated", offer);

            // Before any attempt the current rate is the opening one; the larger of the two also covers a ledger written
            // before the opening rate was recorded.
            int opening = (int)Math.Round(Math.Max(terms.OpeningPrepaymentRate, terms.PrepaymentRate) * 100m, MidpointRounding.AwayFromZero);
            int rank = TrustService.Rank(world);
            if (requestedPercent <= opening || requestedPercent > MaxPercentFor(rank)) return Decision<ContractState>.Fail("RW_Reject_InvalidRate", offer);

            decimal chance = SuccessChance(opening, requestedPercent, socialLevel, rank);
            if (Roll(terms.Seed) < chance)
            {
                terms.PrepaymentRate = requestedPercent / 100m;
                terms.CompletionRate = CompletionRateAfter(terms.CompletionRate, requestedPercent - opening);
                terms.NominalTotalReward = Accounting.NominalTotalReward(terms);
                offer.Negotiation = NegotiationState.Succeeded;
            }
            else
            {
                offer.Negotiation = NegotiationState.Failed;
            }
            return Decision<ContractState>.Ok(offer);
        }
    }
}
