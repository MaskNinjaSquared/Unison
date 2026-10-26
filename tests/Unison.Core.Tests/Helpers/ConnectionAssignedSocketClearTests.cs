using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionAssignedSocketClearTests
    {
        [Fact]
        public void Abort_journals_then_clears()
        {
            var host = new FakeClearHost();
            ConnectionAssignedSocketClear.Abort(host, "fatal-session-ended-before-socket");

            Assert.Equal("fatal-session-ended-before-socket", host.LastReason);
            Assert.Equal(1, host.ClearCount);
        }

        [Fact]
        public void AfterFailedConnect_clears_without_journal()
        {
            var host = new FakeClearHost();
            ConnectionAssignedSocketClear.AfterFailedConnect(host);

            Assert.Null(host.LastReason);
            Assert.Equal(1, host.ClearCount);
        }

        [Fact]
        public void Requires_host()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => ConnectionAssignedSocketClear.Abort(null, "x"));
            Assert.Throws<System.ArgumentNullException>(
                () => ConnectionAssignedSocketClear.AfterFailedConnect(null));
        }

        private sealed class FakeClearHost : IConnectionAssignedSocketClearHost
        {
            public string LastReason { get; private set; }

            public int ClearCount { get; private set; }

            public void NoteConnectAborted(string journalReason) => LastReason = journalReason;

            public void DisposeAndClearAssignedSocket() => ClearCount++;
        }
    }
}
