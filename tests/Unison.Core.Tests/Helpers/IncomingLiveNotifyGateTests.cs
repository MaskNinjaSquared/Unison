using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingLiveNotifyGateTests
    {
        [Fact]
        public void From_me_does_not_announce()
        {
            Assert.False(IncomingLiveNotifyGate.ShouldAnnounce(isFromMe: true));
        }

        [Fact]
        public void Inbound_announces()
        {
            Assert.True(IncomingLiveNotifyGate.ShouldAnnounce(isFromMe: false));
        }
    }
}
