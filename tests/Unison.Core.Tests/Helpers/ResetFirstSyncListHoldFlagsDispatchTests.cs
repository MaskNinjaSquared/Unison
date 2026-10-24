using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ResetFirstSyncListHoldFlagsDispatchTests
    {
        [Fact]
        public void Clears_flags_and_logs()
        {
            var host = new FakeHost();
            ResetFirstSyncListHoldFlagsDispatch.Run(host, "done");
            Assert.True(host.Cleared);
            Assert.Equal("done", host.LoggedReason);
        }

        private sealed class FakeHost : IResetFirstSyncListHoldFlagsHost
        {
            public bool Cleared;
            public string LoggedReason;

            public void ClearFirstSyncListHoldFlags() => Cleared = true;

            public void LogFirstSyncListHoldFlagsReset(string reason) =>
                LoggedReason = reason;
        }
    }
}
