using System;

namespace RentalWarehouse.Core
{
    /// <summary>Fee, completion, compensation and goodwill arithmetic from spec 5.</summary>
    public static class Accounting
    {
        public static decimal DailyFee(ContractTerms terms, decimal cargoValue) =>
            (terms.DailyBase + cargoValue * terms.DailyValueRate) * terms.FeeMultiplier;

        /// <summary>Daily fee of every crate together, as if all were stored.</summary>
        public static decimal FullDailyFee(ContractTerms terms)
        {
            decimal sum = 0m;
            foreach (var v in terms.CargoValues) sum += DailyFee(terms, v);
            return sum;
        }

        public static decimal FeeForTicks(ContractTerms terms, decimal cargoValue, long ticks) =>
            DailyFee(terms, cargoValue) * ticks / Time.TicksPerDay;

        /// <summary>Full-term, all-raids, full-return reward; the cap for ordinary loss compensation.</summary>
        public static decimal NominalTotalReward(ContractTerms terms) =>
            FullDailyFee(terms) * terms.DurationTicks / Time.TicksPerDay * (1m + terms.CompletionRate);

        public static decimal RaidFulfilment(int scheduled, int started)
        {
            if (scheduled <= 0) return 1m;
            return Math.Min(1m, (decimal)Math.Max(0, started) / scheduled);
        }

        public static decimal ReturnValueRate(ContractState state)
        {
            decimal total = state.Terms.TotalValue;
            if (total <= 0m) return 0m;
            decimal returned = 0m;
            foreach (var c in state.CargoStates)
            {
                if (c.Returned) returned += c.DeclaredValue;
            }
            return returned / total;
        }

        public static SettlementResult Settle(ContractState state)
        {
            var terms = state.Terms;
            var result = new SettlementResult
            {
                AccruedFee = state.AccruedFee,
                PreviouslyClaimed = state.ClaimedFee,
                ReturnValueRate = ReturnValueRate(state),
            };
            bool systemCancelled = state.SettlementCause == SettlementCause.SystemCancelled;

            if (state.ReturnReason == ReturnReason.Matured && !systemCancelled)
            {
                result.CompletionBonus = state.AccruedFee * terms.CompletionRate * result.ReturnValueRate
                    * RaidFulfilment(terms.ScheduledRaidCount, state.StartedRaids);
            }

            if (state.ReturnReason == ReturnReason.EarlyByPlayer && !systemCancelled)
            {
                result.CancellationFee = Math.Min(state.AccruedFee, FullDailyFee(terms) * Limits.CancellationFeeDays);
            }

            decimal lostValue = 0m, embezzledValue = 0m;
            foreach (var c in state.CargoStates)
            {
                if (c.Loss == LossCause.Lost) lostValue += c.DeclaredValue;
                else if (c.Loss == LossCause.Embezzled) embezzledValue += c.DeclaredValue;
            }

            if (!systemCancelled)
            {
                decimal normal = Math.Min(lostValue * Limits.LossCompensationRate, terms.NominalTotalReward);
                result.Compensation = normal + embezzledValue * Limits.EmbezzleCompensationRate;
            }

            decimal net = decimal.Truncate(result.AccruedFee + result.CompletionBonus - result.CancellationFee
                - result.PreviouslyClaimed - result.Compensation);
            result.Credit = Math.Max(0m, net);
            result.Debt = Math.Max(0m, -net);
            result.GoodwillDelta = systemCancelled ? 0 : Goodwill(state, result.ReturnValueRate, lostValue, embezzledValue);
            return result;
        }

        private static int Goodwill(ContractState state, decimal returnRate, decimal lostValue, decimal embezzledValue)
        {
            if (embezzledValue > 0m) return Limits.EmbezzleGoodwill;
            if (lostValue > 0m) return -(int)Math.Ceiling(Limits.LossGoodwillScale * (1m - returnRate));
            if (state.ReturnReason == ReturnReason.Matured) return Limits.CompletionGoodwill;
            return 0;
        }
    }
}
