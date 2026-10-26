using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MarkPairingRestartPendingDispatchTests
    {
        [Fact]
        public void First_mark_traces()
        {
            var host = new FakeHost { PairingRestartPending = false };
            MarkPairingRestartPendingDispatch.Run(host, "515");
            Assert.True(host.PairingRestartPending);
            Assert.True(host.FatalCleared);
            Assert.True(host.StreakReset);
            Assert.Equal("515", host.TracedReason);
        }

        [Fact]
        public void Already_pending_skips_trace()
        {
            var host = new FakeHost { PairingRestartPending = true };
            MarkPairingRestartPendingDispatch.Run(host, "again");
            Assert.Null(host.TracedReason);
        }

        private sealed class FakeHost : IMarkPairingRestartPendingHost
        {
            public bool PairingRestartPending { get; set; }
            public bool FatalCleared;
            public bool StreakReset;
            public string TracedReason;

            public void ClearPreSessionCloseAsFatal() => FatalCleared = true;

            public void ResetPreSessionCloseStreak() => StreakReset = true;

            public void TracePairingRestartPending(string reason) =>
                TracedReason = reason;
        }
    }
}
