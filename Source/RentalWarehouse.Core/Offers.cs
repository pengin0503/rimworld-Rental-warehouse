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
    /// Offer schedule (spec 4): first batch one day after opening, then one every 1–3 days, at most two per batch and four
    /// open at once. Each batch, and the gap after it, draws from a seed derived from the world seed and the batch's place
    /// in the schedule, so closing and reopening the business never rerolls anything.
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

            if (world.BusinessOpen) created.AddRange(MakeBatch(world, ctx, world.NextOfferBatch, now));
            // A batch missed while ticks were skipped is passed over, like a batch due while the business is closed.
            while (world.NextOfferTick.Value <= now)
            {
                world.NextOfferTick += OfferInterval(world.WorldSeed, world.NextOfferBatch);
                world.NextOfferBatch++;
            }
            return created;
        }

        /// <summary>Gap between batch <paramref name="batch"/> and the next one.</summary>
        public static long OfferInterval(int worldSeed, int batch)
        {
            var rng = new DeterministicRandom(DeterministicRandom.Mix(worldSeed, "offer-interval:" + batch.ToString(CultureInfo.InvariantCulture)));
            return rng.Range(Limits.MinOfferInterval, Limits.MaxOfferInterval);
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

        private static List<ContractTerms> MakeBatch(WorldState world, OfferContext ctx, int batch, long now)
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
            var row = TrustService.Row(world);
            var candidates = kind == ContractKind.HighRisk ? depositors.FindAll(d => Pursuers(ctx, d).Count > 0) : depositors;
            if (candidates.Count == 0) return null;
            var depositor = candidates[rng.Next(candidates.Count)];
            var pursuers = Pursuers(ctx, depositor);
            string? pursuer = pursuers.Count > 0 ? pursuers[rng.Next(pursuers.Count)].FactionId : null;

            int raids = kind == ContractKind.HighRisk ? 2 : (pursuer != null && rng.Chance(0.2m) ? 1 : 0);
            decimal min = kind == ContractKind.HighRisk ? row.HighRiskMin : row.NormalMin;
            decimal max = kind == ContractKind.HighRisk ? row.HighRiskMax : row.NormalMax;
            // Crates are filled to 90-100% of their target, so the target starts at min / 0.9; a row whose min / 0.9 is above
            // its max aims at the max instead and never exceeds it.
            decimal total = rng.Range(Math.Min(min / ManifestBuilder.MinFillRatio, max), max);
            int crates = rng.Range(Limits.MinCrates, row.MaxCrates);
            int seed = rng.Next(int.MaxValue);
            long duration = p.DrawDurationTicks(rng);
            decimal prepayment = rng.Range(0, Limits.MaxPrepaymentPercent) / 100m;

            for (int n = crates; n <= row.MaxCrates; n++)
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
                var terms = ContractTermsFactory.Create(p, contractId, depositor.FactionId, pursuer, cargo, raids, duration, prepayment, world.Settings, world.SettingsVersion, seed, now);
                world.Offers.Add(new ContractState { Terms = terms, Phase = ContractPhase.Offered });
                return terms;
            }
            return null;
        }
    }
}
