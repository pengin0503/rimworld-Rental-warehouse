using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Maps game objects to the string IDs the Core keeps. Core never holds game references.</summary>
    public static class Ids
    {
        private const string MapPrefix = "Map_";
        private const string FactionPrefix = "Faction_";

        // The ledger asks for these IDs from every tick and every UI frame, so each is built once per object.
        private static readonly ConditionalWeakTable<Map, string> mapIds = new ConditionalWeakTable<Map, string>();
        private static readonly ConditionalWeakTable<Faction, string> factionIds = new ConditionalWeakTable<Faction, string>();

        public static string Of(Map map) => map == null ? null : mapIds.GetValue(map, m => MapPrefix + m.uniqueID);

        public static string Of(Faction faction)
        {
            if (faction == null) return null;
            return faction.IsPlayer ? CargoService.PlayerFactionId : factionIds.GetValue(faction, f => f.GetUniqueLoadID());
        }

        public static Map Map(string id)
        {
            if (!TryParseNumber(id, MapPrefix, out int number)) return null;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].uniqueID == number) return maps[i];
            }
            return null;
        }

        public static Faction Faction(string id)
        {
            if (id == null) return null;
            if (id == CargoService.PlayerFactionId) return RimWorld.Faction.OfPlayer;
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
            if (TryParseNumber(id, FactionPrefix, out int number))
            {
                for (int i = 0; i < factions.Count; i++)
                {
                    // The ID text is checked too, in case another build formats it differently; it is cached, so the check is cheap.
                    if (factions[i].loadID == number && Of(factions[i]) == id) return factions[i];
                }
            }
            for (int i = 0; i < factions.Count; i++)
            {
                if (Of(factions[i]) == id) return factions[i];
            }
            return null;
        }

        /// <summary>Reads the non-negative number after <paramref name="prefix"/> without allocating; false for anything else.</summary>
        public static bool TryParseNumber(string id, string prefix, out int number)
        {
            number = 0;
            if (id == null || id.Length <= prefix.Length || !id.StartsWith(prefix, System.StringComparison.Ordinal)) return false;
            for (int i = prefix.Length; i < id.Length; i++)
            {
                int digit = id[i] - '0';
                if (digit < 0 || digit > 9 || number > (int.MaxValue - digit) / 10) return false;
                number = number * 10 + digit;
            }
            return true;
        }

        public static long Now => Find.TickManager.TicksGame;
    }
}
