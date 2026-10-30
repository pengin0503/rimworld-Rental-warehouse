namespace RentalWarehouse.Core
{
    /// <summary>
    /// When cargo thieves give up (issue #11). Like an ordinary raid they leave after a while and after heavy losses; they also
    /// leave once no contract crate is left to steal, after a short grace so a crate being handed over is not mistaken for none.
    /// </summary>
    public static class RaidRetreat
    {
        /// <summary>Ticks after which the raiders give up whatever happens (about two thirds of a day, like an ordinary assault).</summary>
        public const int GiveUpTicks = 40000;
        /// <summary>Share of the raiders downed or dead at which the rest retreat.</summary>
        public const decimal CasualtyFraction = 0.5m;
        /// <summary>How long nothing may be left to steal before the raiders leave.</summary>
        public const long NothingToStealGraceTicks = 600;

        /// <summary>
        /// One look at the map. <paramref name="since"/> is when nothing was last first seen to be left to steal, or null while
        /// something is. Returns the updated marker and whether the raiders should retreat now. A raider carrying a crate leaves on
        /// its own, so that never starts the clock.
        /// </summary>
        public static (long? Since, bool Retreat) Observe(long? since, bool stealableLeft, bool someoneCarrying, long now)
        {
            if (stealableLeft || someoneCarrying) return (null, false);
            long start = since ?? now;
            return (start, now - start >= NothingToStealGraceTicks);
        }
    }
}
