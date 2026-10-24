using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class CompleteUserResyncHistoryWaitDispatchTests
    {
        [Fact]
        public void No_waiter_is_noop()
        {
            var host = new FakeHost { HasWaiter = false };
            CompleteUserResyncHistoryWaitDispatch.Run(host, "t");
            Assert.False(host.ClearedRepair);
            Assert.False(host.Completed);
        }

        [Fact]
        public void Waiter_clears_repair_logs_and_completes()
        {
            var host = new FakeHost { HasWaiter = true };
            CompleteUserResyncHistoryWaitDispatch.Run(host, "arrived");
            Assert.True(host.ClearedRepair);
            Assert.Equal("arrived", host.LoggedReason);
            Assert.True(host.Completed);
        }

        private sealed class FakeHost : ICompleteUserResyncHistoryWaitHost
        {
            public bool HasWaiter;
            public bool ClearedRepair;
            public bool Completed;
            public string LoggedReason;

            public bool TryClaimUserResyncHistoryWaiter(out object waiter)
            {
                waiter = HasWaiter ? new object() : null;
                return HasWaiter;
            }

            public void ClearForceHistoryRepairPendingBestEffort() => ClearedRepair = true;

            public void LogUserResyncHistoryWaitCompleted(string reason) =>
                LoggedReason = reason;

            public void CompleteUserResyncHistoryWaiter(object waiter) => Completed = waiter != null;
        }
    }
}
