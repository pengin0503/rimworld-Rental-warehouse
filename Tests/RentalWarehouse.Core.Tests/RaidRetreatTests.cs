using RentalWarehouse.Core;
using Xunit;

namespace RentalWarehouse.Core.Tests
{
    /// <summary>Cargo thieves give up once there is nothing left to steal (issue #11).</summary>
    public class RaidRetreatTests
    {
        private const long Grace = RaidRetreat.NothingToStealGraceTicks;

        [Fact]
        public void WhileSomethingCanStillBeStolen_NobodyRetreats_AndTheClockStaysOff()
        {
            var (since, retreat) = RaidRetreat.Observe(null, stealableLeft: true, someoneCarrying: false, now: 5000);

            Assert.Null(since);
            Assert.False(retreat);
        }

        [Fact]
        public void TheFirstEmptyLook_StartsTheClock_ButDoesNotRetreatYet()
        {
            var (since, retreat) = RaidRetreat.Observe(null, stealableLeft: false, someoneCarrying: false, now: 5000);

            Assert.Equal(5000L, since);
            Assert.False(retreat);
        }

        [Fact]
        public void ARetreatStartsOnlyOnceTheGracePeriodHasPassed()
        {
            var (since, early) = RaidRetreat.Observe(5000, false, false, 5000 + Grace - 1);
            var (_, late) = RaidRetreat.Observe(since, false, false, 5000 + Grace);

            Assert.False(early);
            Assert.True(late);
        }

        [Fact]
        public void ACrateBeingCarried_KeepsTheClockOff_SoTheCarrierCanLeaveByItself()
        {
            var (since, retreat) = RaidRetreat.Observe(5000, stealableLeft: false, someoneCarrying: true, now: 5000 + 10 * Grace);

            Assert.Null(since);
            Assert.False(retreat);
        }

        [Fact]
        public void ACrateBecomingStealableAgain_ResetsTheClock()
        {
            var (since, retreat) = RaidRetreat.Observe(5000, stealableLeft: true, someoneCarrying: false, now: 5000 + 10 * Grace);

            Assert.Null(since);
            Assert.False(retreat);
        }

        [Fact]
        public void TheThresholds_AreSane()
        {
            Assert.InRange(RaidRetreat.GiveUpTicks, CoreTime.TicksPerDay / 2, 2 * CoreTime.TicksPerDay);
            Assert.InRange(RaidRetreat.CasualtyFraction, 0.25m, 0.75m);
            Assert.InRange(Grace, 250, CoreTime.TicksPerDay / 4);
        }
    }
}
