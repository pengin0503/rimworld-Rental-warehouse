using System.Collections.Generic;
using RentalWarehouse.Core;
using RimWorld;
using Verse;

namespace RentalWarehouse
{
    /// <summary>Maps game objects to the string IDs the Core keeps. Core never holds game references.</summary>
    public static class Ids
    {
        public static string Of(Map map) => map == null ? null : "Map_" + map.uniqueID;

        public static string Of(Faction faction)
        {
            if (faction == null) return null;
            return faction.IsPlayer ? CargoService.PlayerFactionId : faction.GetUniqueLoadID();
        }

        public static Map Map(string id)
        {
            if (id == null) return null;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                if (Of(maps[i]) == id) return maps[i];
            }
            return null;
        }

        public static Faction Faction(string id)
        {
            if (id == null) return null;
            if (id == CargoService.PlayerFactionId) return RimWorld.Faction.OfPlayer;
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < factions.Count; i++)
            {
                if (Of(factions[i]) == id) return factions[i];
            }
            return null;
        }

        public static long Now => Find.TickManager.TicksGame;
    }
}
