using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryCatchUpRepairCascadeTests
    {
        [Fact]
        public void Empty_ack_ghost_skips_cascade_even_when_still_stale_on_High()
        {
            // Desktop health: FinishStagnantProgress after empty-ack, stillStale=no-stored-messages,
            // AllowStagnantHistoryRepairCascade=true — must not re-fire hydration-then-full.
            Assert.False(HistoryCatchUpRepairCascade.ShouldStartAfterFinish(
                stillStale: true,
                allowCascade: true,
                ackAccepted: true,
                progressSignals: 0));
        }

        [Fact]
        public void Stale_with_chunk_signals_may_cascade_on_High()
        {
            Assert.True(HistoryCatchUpRepairCascade.ShouldStartAfterFinish(
                stillStale: true,
                allowCascade: true,
                ackAccepted: true,
                progressSignals: 3));
        }

        [Fact]
        public void Fresh_tips_never_cascade()
        {
            Assert.False(HistoryCatchUpRepairCascade.ShouldStartAfterFinish(
                stillStale: false,
                allowCascade: true,
                ackAccepted: true,
                progressSignals: 0));
        }

        [Fact]
        public void Low_workload_cascade_flag_off_never_starts()
        {
            Assert.False(HistoryCatchUpRepairCascade.ShouldStartAfterFinish(
                stillStale: true,
                allowCascade: false,
                ackAccepted: false,
                progressSignals: 5));
        }
    }
}
