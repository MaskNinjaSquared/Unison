using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class InvalidateCurrentSocketDispatchTests
    {
        [Fact]
        public void Null_socket_is_noop()
        {
            var host = new FakeHost();
            InvalidateCurrentSocketDispatch.Run(host, null, "x");
            Assert.False(host.Disposed);
        }

        [Fact]
        public void Current_socket_stops_health_and_clears()
        {
            var socket = new object();
            var host = new FakeHost { Current = socket };
            InvalidateCurrentSocketDispatch.Run(host, socket, "stale");
            Assert.Equal("invalidate:stale", host.HealthStopReason);
            Assert.True(host.Cleared);
            Assert.True(host.Disposed);
            Assert.Equal("stale", host.JournalReason);
        }

        [Fact]
        public void Non_current_still_disposes()
        {
            var host = new FakeHost { Current = new object() };
            InvalidateCurrentSocketDispatch.Run(host, new object(), "old");
            Assert.Null(host.HealthStopReason);
            Assert.False(host.Cleared);
            Assert.True(host.Disposed);
        }

        private sealed class FakeHost : IInvalidateCurrentSocketHost
        {
            public object Current;
            public string HealthStopReason;
            public bool Cleared;
            public bool Disposed;
            public string JournalReason;

            public bool IsCurrentSocket(object socket) =>
                ReferenceEquals(Current, socket);

            public void StopConnectionHealthMonitor(string reason) =>
                HealthStopReason = reason;

            public void ClearCurrentSocketField() => Cleared = true;

            public void DisconnectAndDisposeSocket(object socket) => Disposed = true;

            public void WriteSocketInvalidatedJournal(string reason) =>
                JournalReason = reason;
        }
    }
}
