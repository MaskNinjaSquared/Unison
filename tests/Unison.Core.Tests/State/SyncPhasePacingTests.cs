using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public sealed class SyncPhasePacingTests
    {
        [Fact]
        public void Without_list_hold_returns_tier_frugal_pacing()
        {
            SyncPacing expected = LowSyncWorkload.Instance.PacingFor(frugal: true);

            SyncPacing actual = SyncPhasePacing.For(
                LowSyncWorkload.Instance,
                frugal: true,
                firstSyncListHold: false);

            Assert.Equal(expected.StartupQuietFloorMs, actual.StartupQuietFloorMs);
            Assert.Equal(expected.DeferredStartupQuietMs, actual.DeferredStartupQuietMs);
            Assert.Equal(expected.CatchUpIdleAbortSeconds, actual.CatchUpIdleAbortSeconds);
            Assert.Equal(expected.CatchUpStagnantCyclesBeforeStop, actual.CatchUpStagnantCyclesBeforeStop);
        }

        [Fact]
        public void List_hold_shortens_quiet_floors_without_touching_catch_up_abort()
        {
            SyncPacing frugal = LowSyncWorkload.Instance.PacingFor(frugal: true);

            SyncPacing hold = SyncPhasePacing.For(
                LowSyncWorkload.Instance,
                frugal: true,
                firstSyncListHold: true);

            Assert.Equal(SyncPhasePacing.HoldStartupQuietFloorMs, hold.StartupQuietFloorMs);
            Assert.Equal(SyncPhasePacing.HoldDeferredStartupQuietMs, hold.DeferredStartupQuietMs);
            Assert.True(hold.StartupQuietFloorMs < frugal.StartupQuietFloorMs);
            Assert.True(hold.DeferredStartupQuietMs < frugal.DeferredStartupQuietMs);
            Assert.Equal(frugal.CatchUpIdleAbortSeconds, hold.CatchUpIdleAbortSeconds);
            Assert.Equal(frugal.CatchUpStagnantCyclesBeforeStop, hold.CatchUpStagnantCyclesBeforeStop);
        }

        [Fact]
        public void List_hold_does_not_lengthen_already_shorter_generous_floors()
        {
            SyncPacing generous = HighSyncWorkload.Instance.PacingFor(frugal: false);

            SyncPacing hold = SyncPhasePacing.For(
                HighSyncWorkload.Instance,
                frugal: false,
                firstSyncListHold: true);

            Assert.Equal(generous.StartupQuietFloorMs, hold.StartupQuietFloorMs);
            Assert.Equal(generous.DeferredStartupQuietMs, hold.DeferredStartupQuietMs);
        }
    }
}
