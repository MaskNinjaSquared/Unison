using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EndHistoryCatchUpBannerDispatchTests
    {
        [Fact]
        public void Inactive_banner_is_noop()
        {
            var host = new FakeHost { Ended = false };
            EndHistoryCatchUpBannerDispatch.Run(host, "t");
            Assert.False(host.NamesApplied);
            Assert.False(host.JournalWritten);
        }

        [Fact]
        public void Active_banner_writes_and_applies_names()
        {
            var host = new FakeHost { Ended = true };
            EndHistoryCatchUpBannerDispatch.Run(host, "done");
            Assert.True(host.JournalWritten);
            Assert.True(host.NamesApplied);
        }

        private sealed class FakeHost : IEndHistoryCatchUpBannerHost
        {
            public bool Ended;
            public bool JournalWritten;
            public bool NamesApplied;

            public bool TryEndHistoryCatchUpBanner() => Ended;

            public void WriteCatchUpBannerEnd(string reason) => JournalWritten = true;

            public void FireAndForgetApplyResolvedNamesToChatList() => NamesApplied = true;
        }
    }
}
