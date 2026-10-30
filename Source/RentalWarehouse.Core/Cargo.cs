using System;
using System.Collections.Generic;

namespace RentalWarehouse.Core
{
    /// <summary>A kind of goods a crate may contain, priced by the game adapter.</summary>
    public sealed class Good
    {
        public Good(string defName, string? stuffDefName, decimal unitValue, decimal unitMass, int? quality = null)
        {
            DefName = defName;
            StuffDefName = stuffDefName;
            UnitValue = unitValue;
            UnitMass = unitMass;
            Quality = quality;
        }

        public string DefName { get; }
        public string? StuffDefName { get; }
        public decimal UnitValue { get; }
        public decimal UnitMass { get; }
        public int? Quality { get; }
    }

    /// <summary>SplitMix64. Independent of the runtime's Random so saved seeds reproduce across platforms.</summary>
    public sealed class DeterministicRandom
    {
        private ulong _state;

        public DeterministicRandom(int seed)
        {
            _state = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
        }

        public static int Mix(int seed, string salt)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (char ch in salt) h = (h ^ ch) * 16777619u;
                return (int)(h ^ (uint)seed * 0x85EBCA6Bu);
            }
        }

        private ulong NextULong()
        {
            unchecked
            {
                ulong z = _state += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public int Next(int maxExclusive)
        {
            if (maxExclusive <= 0) return 0;
            return (int)(NextULong() % (ulong)maxExclusive);
        }

        public int Range(int minInclusive, int maxInclusive) => minInclusive + Next(maxInclusive - minInclusive + 1);

        public long Range(long minInclusive, long maxInclusive)
        {
            if (maxInclusive <= minInclusive) return minInclusive;
            return minInclusive + (long)(NextULong() % (ulong)(maxInclusive - minInclusive + 1));
        }

        /// <summary>Uniform in [0, 1) with 1/1,000,000 resolution.</summary>
        public decimal NextDecimal() => Next(1000000) / 1000000m;

        public decimal Range(decimal min, decimal max) => min + (max - min) * NextDecimal();

        public bool Chance(decimal probability) => NextDecimal() < probability;

        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    /// <summary>Builds crate manifests whose value lands within 90–100% of a target without exceeding the mass limit.</summary>
    public static class ManifestBuilder
    {
        public const decimal MinFillRatio = 0.9m;

        public static List<(List<ManifestEntry> Manifest, decimal Value, decimal Mass)>? Build(
            IList<Good> goods, IList<decimal> targetValues, decimal maxMass, DeterministicRandom rng)
        {
            var result = new List<(List<ManifestEntry>, decimal, decimal)>();
            foreach (decimal target in targetValues)
            {
                var crate = BuildOne(goods, target, maxMass, rng);
                if (crate == null) return null;
                result.Add(crate.Value);
            }
            return result;
        }

        private static (List<ManifestEntry>, decimal, decimal)? BuildOne(IList<Good> goods, decimal target, decimal maxMass, DeterministicRandom rng)
        {
            var order = new List<Good>();
            foreach (var g in goods)
            {
                if (g.UnitValue > 0m && g.UnitMass >= 0m) order.Add(g);
            }
            rng.Shuffle(order);

            // Try each good as the main content, topping up with the others in shuffled order.
            for (int start = 0; start < order.Count; start++)
            {
                var entries = new List<ManifestEntry>();
                decimal value = 0m, mass = 0m;
                for (int k = 0; k < order.Count; k++)
                {
                    var g = order[(start + k) % order.Count];
                    decimal byValue = decimal.Floor((target - value) / g.UnitValue);
                    decimal byMass = g.UnitMass > 0m ? decimal.Floor((maxMass - mass) / g.UnitMass) : byValue;
                    int count = (int)Math.Min(byValue, byMass);
                    if (count <= 0) continue;
                    entries.Add(new ManifestEntry { ThingDefName = g.DefName, StuffDefName = g.StuffDefName, Quality = g.Quality, Count = count });
                    value += g.UnitValue * count;
                    mass += g.UnitMass * count;
                    if (value >= target * MinFillRatio) break;
                }
                if (value >= target * MinFillRatio && value <= target && mass <= maxMass) return (entries, value, mass);
            }
            return null;
        }
    }

    public static class CargoService
    {
        public const string PlayerFactionId = "Player";

        /// <summary>
        /// Wealth contribution (spec 3): crates held for another faction count 0, crates the player owns count their
        /// declared value until opened, after which the spawned contents count instead.
        /// </summary>
        public static decimal WealthValue(CargoRecord record)
        {
            if (record.Opened) return 0m;
            return record.Custody == CargoCustody.Player ? record.DeclaredValue : 0m;
        }

        /// <summary>Moves an NPC vault crate into the player's ownership, keeping the original owner.</summary>
        public static Decision<CargoRecord> TakeFromSite(CargoRecord record)
        {
            if (record.Custody != CargoCustody.Depositor || record.Opened || record.ContractId != null)
            {
                return Decision<CargoRecord>.Fail("RW_Reject_CargoNotHeld", record);
            }
            record.OriginalOwnerFactionId = record.OwnerFactionId;
            record.OwnerFactionId = PlayerFactionId;
            record.Custody = CargoCustody.Player;
            record.Stolen = true;
            record.ContainerId = null;
            return Decision<CargoRecord>.Ok(record);
        }

        /// <summary>Breaks the seal once. The caller decides whether this is embezzlement (contract crate) or looting.</summary>
        public static Decision<CargoRecord> Open(CargoRecord record, string effectId)
        {
            if (record.Opened || record.Custody == CargoCustody.Destroyed || record.Custody == CargoCustody.Returned)
            {
                return Decision<CargoRecord>.Fail("RW_Reject_CannotOpen", record);
            }
            record.Opened = true;
            return Decision<CargoRecord>.Ok(record, new List<EffectRequest>
            {
                new EffectRequest { EffectId = effectId, Kind = EffectKind.ConsumeCargo, EntityId = record.CargoId },
            });
        }

        /// <summary>Returns a reason key, or null when the crate is valid.</summary>
        public static string? Validate(CargoRecord record)
        {
            if (string.IsNullOrEmpty(record.CargoId) || record.DeclaredValue < 0m) return "RW_Reject_InvalidTerms";
            if (record.MassKg > Limits.MaxCrateMassKg) return "RW_Reject_TooHeavy";
            if (record.Manifest.Count == 0) return "RW_Reject_InvalidManifest";
            foreach (var m in record.Manifest)
            {
                if (string.IsNullOrEmpty(m.ThingDefName) || m.Count <= 0) return "RW_Reject_InvalidManifest";
            }
            return null;
        }

        /// <summary>A destroyed rack or vault drops its crates once; the crates themselves are unaffected.</summary>
        public static List<string> EjectFromContainer(WorldState world, string containerId)
        {
            var ejected = new List<string>();
            foreach (var record in world.Cargo.Values)
            {
                if (record.ContainerId != containerId) continue;
                record.ContainerId = null;
                ejected.Add(record.CargoId);
            }
            return ejected;
        }

        /// <summary>Records crate damage. Returns true only on the transition to destroyed.</summary>
        public static bool ApplyHitPoints(CargoRecord record, int hitPoints)
        {
            if (record.Custody == CargoCustody.Destroyed) return false;
            record.HitPoints = Math.Max(0, hitPoints);
            if (record.HitPoints > 0) return false;
            record.Custody = CargoCustody.Destroyed;
            return true;
        }
    }
}
