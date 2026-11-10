using System;
using System;
using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayUiPolicyTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void SkipCatalogueReposition_only_when_frugal(bool preferFrugal, bool expectSkip)
        {
            Assert.Equal(expectSkip, OfflineReplayUiPolicy.SkipCatalogueReposition(preferFrugal));
        }

        [Theory]
        [InlineData(false, "replay-progressive", false)]
        [InlineData(true, "replay-progressive", true)]
        [InlineData(true, "offline-complete:0", false)]
        [InlineData(true, "settle", false)]
        public void SkipProgressiveTipHeal_only_frugal_progressive(
            bool preferFrugal,
            string reason,
            bool expectSkip)
        {
            Assert.Equal(
                expectSkip,
                OfflineReplayUiPolicy.SkipProgressiveTipHeal(preferFrugal, reason));
        }

        [Theory]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        public void DeferTipPaintUntilSettle_only_while_frugal_replay_drain_is_active(
            bool preferFrugal,
            bool replayDrainActive,
            bool expectDefer)
        {
            Assert.Equal(
                expectDefer,
                OfflineReplayUiPolicy.DeferTipPaintUntilSettle(preferFrugal, replayDrainActive));
        }

        [Theory]
        [InlineData(false, true, 500)]
        [InlineData(true, true, 2000)]
        [InlineData(true, false, 0)]
        public void TipBusyQuietAfterFlush_is_zero_after_frugal_settle(
            bool preferFrugal,
            bool replayDrainActive,
            int expectQuietMs)
        {
            Assert.Equal(
                TimeSpan.FromMilliseconds(expectQuietMs),
                OfflineReplayUiPolicy.TipBusyQuietAfterFlush(preferFrugal, replayDrainActive));
        }

        [Theory]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        public void IsOfflineEnvelope_is_append_not_from_me(
            bool upsertIsAppend,
            bool fromMe,
            bool expectOffline)
        {
            Assert.Equal(
                expectOffline,
                OfflineReplayUiPolicy.IsOfflineEnvelope(upsertIsAppend, fromMe));
        }
    }
}
