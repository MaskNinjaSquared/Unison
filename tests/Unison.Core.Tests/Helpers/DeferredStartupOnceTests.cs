using Unison.WhatsAppClient.Connection;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class DeferredStartupOnceTests
    {
        [Fact]
        public void TryStart_returns_true_once()
        {
            var once = new DeferredStartupOnce();

            Assert.True(once.TryStart());
            Assert.False(once.TryStart());
            Assert.False(once.TryStart());
        }
    }
}
