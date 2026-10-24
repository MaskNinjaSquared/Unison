using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BeginHistoryCatchUpBannerDispatchTests
    {
        [Fact]
        public void Latches_notes_progress_and_writes_journal()
        {
            var host = new FakeHost();
            BeginHistoryCatchUpBannerDispatch.Run(host, "sync");
            Assert.True(host.Latched);
            Assert.Equal("banner-begin:sync", host.ProgressReason);
            Assert.Equal("sync", host.JournalReason);
        }

        [Fact]
        public void Null_reason_still_latches()
        {
            var host = new FakeHost();
            BeginHistoryCatchUpBannerDispatch.Run(host, null);
            Assert.True(host.Latched);
            Assert.Equal("banner-begin:", host.ProgressReason);
        }

        private sealed class FakeHost : IBeginHistoryCatchUpBannerHost
        {
            public bool Latched;
            public string ProgressReason;
            public string JournalReason;

            public void LatchHistoryCatchUpBanner() => Latched = true;

            public void NoteFullHistoryCatchUpProgress(string reason) =>
                ProgressReason = reason;

            public void WriteCatchUpBannerBegin(string reason) =>
                JournalReason = reason;
        }
    }
}
