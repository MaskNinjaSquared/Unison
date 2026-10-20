// =============================================================================
// Tests for HistoryCatchUpLocalWorkPolicy.
//
// Post-reconnect preview reconcile and usync enrichment must not run while
// FULL_HISTORY catch-up is still owed — they starve the backlog on Mobile.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryCatchUpLocalWorkPolicyTests
    {
        [Fact]
        public void Heavy_local_work_runs_when_catch_up_is_idle()
        {
            Assert.False(HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(false));
            Assert.Null(HistoryCatchUpLocalWorkPolicy.DeferReason(false));
        }

        [Fact]
        public void Heavy_local_work_waits_while_catch_up_is_active()
        {
            // Reconnect with an 11-day backlog: scanning 300+ previews and firing usync
            // before Meta finishes chronological replay is the slow path in device logs.
            Assert.True(HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(true));
            Assert.Equal("history-catch-up", HistoryCatchUpLocalWorkPolicy.DeferReason(true));
        }
    }
}
