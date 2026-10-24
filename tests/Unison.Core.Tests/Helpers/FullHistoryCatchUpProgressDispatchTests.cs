using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullHistoryCatchUpProgressDispatchTests
    {
        [Fact]
        public void Local_tip_noise_is_ignored()
        {
            var host = new FakeHost { BannerActive = true, Claimed = true };
            FullHistoryCatchUpProgressDispatch.Run(host, "tip-swapped:chat");

            Assert.False(host.Noted);
        }

        [Fact]
        public void Without_banner_or_claim_returns()
        {
            var host = new FakeHost();
            FullHistoryCatchUpProgressDispatch.Run(host, "sqlite-applied:FULL");

            Assert.False(host.Noted);
        }

        [Fact]
        public void Claimed_notes_and_journals()
        {
            var host = new FakeHost { Claimed = true, NoteOk = true };
            FullHistoryCatchUpProgressDispatch.Run(host, "sqlite-applied:FULL");

            Assert.True(host.Noted);
            Assert.True(host.Journaled);
        }

        private sealed class FakeHost : IFullHistoryCatchUpProgressHost
        {
            public bool BannerActive;
            public bool Claimed;
            public bool NoteOk;
            public bool Noted;
            public bool Journaled;

            public bool IsHistoryCatchUpBannerActive => BannerActive;
            public bool IsFullHistoryClaimed => Claimed;
            public bool HasFullHistoryRequestId => false;

            public bool NoteFullProgress(
                string reason,
                bool countsAsLot,
                out string requestId,
                out int signalCount)
            {
                Noted = true;
                requestId = "r1";
                signalCount = 1;
                return NoteOk;
            }

            public void WriteCatchUpProgressJournal(
                string requestId,
                int signalCount,
                bool countsAsLot,
                string reason) =>
                Journaled = true;
        }
    }
}
