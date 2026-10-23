using System;
using System.Collections.Generic;
using System.Globalization;

namespace RentalWarehouse.Core
{
    /// <summary>What the game reports about a faction, at the moment offers are generated.</summary>
    public sealed class FactionInfo
    {
        public string FactionId = "";
        public bool Humanlike;
        public bool Defeated;
        public bool HostileToPlayer;
        /// <summary>IDs of non-player factions this faction is hostile to.</summary>
        public HashSet<string> HostileTo = new HashSet<string>();
    }

    public sealed class OfferContext
    {
        public List<FactionInfo> Factions = new List<FactionInfo>();
        /// <summary>Non-perishable goods priced by the game (spec 3).</summary>
        public IList<Good> Goods = new List<Good>();
    }

    /// <summary>
    /// Offer schedule (spec 4): first batch one day after opening, then every two days, at most two per batch and four
    /// open at once. Each batch draws from a seed derived from the world seed and the batch's place in the schedule,
    /// so closing and reopening the business never rerolls anything.
    /// </summary>
    public static class OfferService
    {
        public static void SetBusinessOpen(WorldState world, bool open, long now)
        {
            world.BusinessOpen = open;
            if (open && world.BusinessStartTick == null)
            {
                world.BusinessStartTick = now;
                world.NextOfferTick = now + Limits.FirstOfferDelay;
                world.NextSiteTick = now + VaultSiteParameters.FirstDiscoveryDelay;
            }
        }

        public static IReadOnlyList<ContractTerms> Generate(WorldState world, OfferContext ctx, long now)
        {
            ExpireOffers(world, now);
            var created = new List<ContractTerms>();
            if (world.NextOfferTick == null || world.BusinessStartTick == null || now < world.NextOfferTick.Value) return created;

            long due = world.NextOfferTick.Value;
            long batch = (due - world.BusinessStartTick.Value - Limits.FirstOfferDelay) / Limits.OfferInterval;
            if (world.BusinessOpen) created.AddRange(MakeBatch(world, ctx, batch, now));
            while (world.NextOfferTick.Value <= now) world.NextOfferTick += Limits.OfferInterval;
            return created;
        }

        public static void ExpireOffers(WorldState world, long now)
        {
            for (int i = world.Offers.Count - 1; i >= 0; i--)
            {
                var offer = world.Offers[i];
                if (offer.Terms.OfferExpiresTick > now) continue;
                foreach (var id in offer.Terms.CargoIds) world.Cargo.Remove(id);
                world.Offers.RemoveAt(i);
            }
        }

        private static List<ContractTerms> MakeBatch(WorldState world, OfferContext ctx, long batch, long now)
        {
            var result = new List<ContractTerms>();
            var rng = new DeterministicRandom(DeterministicRandom.Mix(world.WorldSeed, "offers:" + batch.ToString(CultureInfo.InvariantCulture)));
            int slots = Math.Min(Limits.OffersPerBatch, Limits.MaxOpenOffers - world.Offers.Count);

            var depositors = Depositors(world, ctx, now);
            bool normalPossible = world.Settings.NormalContractsEnabled && depositors.Count > 0;
            bool dangerPossible = world.Settings.HighRiskContractsEnabled && depositors.Exists(d => Pursuers(ctx, d).Count > 0);
            var kinds = new List<ContractKind>();
            if (normalPossible && dangerPossible)
            {
                kinds.Add(ContractKind.Normal);
                kinds.Add(ContractKind.HighRisk);
            }
            else if (normalPossible || dangerPossible)
            {
                var only = normalPossible ? ContractKind.Normal : ContractKind.HighRisk;
                for (int i = 0; i < Limits.OffersPerBatch; i++) kinds.Add(only);
            }

            for (int i = 0; i < kinds.Count; i++)
            {
                // Draw every slot's numbers even when the slot is not used, so later slots never shift.
                var slotRng = new DeterministicRandom(rng.Next(int.MaxValue));
                if (i >= slots) continue;
                var terms = MakeOffer(world, ctx, depositors, kinds[i], "RWC-" + batch.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture), slotRng, now);
                if (terms != null) result.Add(terms);
            }
            return result;
        }

        private static List<FactionInfo> Depositors(WorldState world, OfferContext ctx, long now)
        {
            var list = new List<FactionInfo>();
            foreach (var f in ctx.Factions)
            {
                if (!f.Humanlike || f.Defeated || f.HostileToPlayer || f.FactionId == CargoService.PlayerFactionId) continue;
                if (world.FactionBlockedUntil.TryGetValue(f.FactionId, out long until) && now < until) continue;
                list.Add(f);
            }
            return list;
        }

        /// <summary>Humanlike factions hostile to both the player and the depositor (spec 4).</summary>
        public static List<FactionInfo> Pursuers(OfferContext ctx, FactionInfo depositor)
        {
            var list = new List<FactionInfo>();
            foreach (var f in ctx.Factions)
            {
                if (f.Humanlike && !f.Defeated && f.HostileToPlayer && f.FactionId != depositor.FactionId && f.HostileTo.Contains(depositor.FactionId)) list.Add(f);
            }
            return list;
        }

        private static ContractTerms? MakeOffer(WorldState world, OfferContext ctx, List<FactionInfo> depositors, ContractKind kind, string contractId, DeterministicRandom rng, long now)
        {
            var p = ContractParameters.For(kind);
            var candidates = kind == ContractKind.HighRisk ? depositors.FindAll(d => Pursuers(ctx, d).Count > 0) : depositors;
            if (candidates.Count == 0) return null;
            var depositor = candidates[rng.Next(candidates.Count)];
            var pursuers = Pursuers(ctx, depositor);
            string? pursuer = pursuers.Count > 0 ? pursuers[rng.Next(pursuers.Count)].FactionId : null;

            int raids = kind == ContractKind.HighRisk ? 2 : (pursuer != null && rng.Chance(0.2m) ? 1 : 0);
            decimal total = rng.Range(p.MinTotalValue / ManifestBuilder.MinFillRatio, p.MaxTotalValue);
            int crates = rng.Range(Limits.MinCrates, Limits.MaxCrates);
            int seed = rng.Next(int.MaxValue);

            for (int n = crates; n <= Limits.MaxCrates; n++)
            {
                var targets = new List<decimal>();
                for (int i = 0; i < n; i++) targets.Add(decimal.Round(total / n, 2));
                var manifests = ManifestBuilder.Build(ctx.Goods, targets, Limits.MaxCrateMassKg - Limits.CrateTareKg, rng);
                if (manifests == null) continue;

                var cargo = new List<(string, decimal)>();
                for (int i = 0; i < n; i++)
                {
                    string cargoId = contractId + "-" + i.ToString(CultureInfo.InvariantCulture);
                    var (manifest, value, mass) = manifests[i];
                    world.Cargo[cargoId] = new CargoRecord
                    {
                        CargoId = cargoId,
                        ContractId = contractId,
                        OwnerFactionId = depositor.FactionId,
                        Custody = CargoCustody.Depositor,
                        Manifest = manifest,
                        DeclaredValue = value,
                        MassKg = mass + Limits.CrateTareKg,
                        RequiredStorage = p.RequiredStorage,
                    };
                    cargo.Add((cargoId, value));
                }
                var terms = ContractTermsFactory.Create(p, contractId, depositor.FactionId, pursuer, cargo, raids, world.Settings, world.SettingsVersion, seed, now);
                world.Offers.Add(new ContractState { Terms = terms, Phase = ContractPhase.Offered });
                return terms;
            }
            return null;
        }
    }
}
