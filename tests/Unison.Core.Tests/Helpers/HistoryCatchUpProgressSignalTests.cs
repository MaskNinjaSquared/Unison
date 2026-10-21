// =============================================================================
// Tests for HistoryCatchUpProgressSignal — ACK/banner must not count as lot progress.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryCatchUpProgressSignalTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Empty_reason_is_not_lot_progress(string reason)
        {
            Assert.False(HistoryCatchUpProgressSignal.CountsAsLotProgress(reason));
        }

        [Fact]
        public void Null_reason_is_not_lot_progress()
        {
            Assert.False(HistoryCatchUpProgressSignal.CountsAsLotProgress(null));
        }

        [Theory]
        [InlineData("ack-accepted")]
        [InlineData("ACK-ACCEPTED")]
        [InlineData("banner-begin")]
        [InlineData("banner-begin:idle-continue:round=1")]
        public void Ack_and_banner_do_not_count(string reason)
        {
            Assert.False(HistoryCatchUpProgressSignal.CountsAsLotProgress(reason));
        }

        [Theory]
        [InlineData("tip-swapped")]
        [InlineData("tip-swapped:5")]
        [InlineData("TIP-SWAPPED:40")]
        [InlineData("tip-swapped:12")]
        public void Local_tip_reconcile_does_not_count(string reason)
        {
            Assert.False(HistoryCatchUpProgressSignal.CountsAsLotProgress(reason));
            Assert.True(HistoryCatchUpProgressSignal.IsLocalTipNoise(reason));
        }

        [Theory]
        [InlineData("sqlite-applied:Full:conversations=12")]
        [InlineData("sqlite-started:RECENT:conversations=3")]
        [InlineData("offline-drain:8")]
        [InlineData("offline-summary:flush:chats=2")]
        public void Real_payload_notes_count(string reason)
        {
            Assert.True(HistoryCatchUpProgressSignal.CountsAsLotProgress(reason));
            Assert.False(HistoryCatchUpProgressSignal.IsLocalTipNoise(reason));
        }
    }
}
