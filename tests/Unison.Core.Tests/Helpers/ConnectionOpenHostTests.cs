using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Connection;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionOpenHostTests
    {
        [Fact]
        public async Task Forwards_open_sequence_through_assignment_port()
        {
            var assignment = new FakeAssignment();
            var host = new ConnectionOpenHost(assignment);
            var sockets = new ForwardingSockets();

            await ConnectionConnectOpen.RunAsync(host, sockets, reuseLoadedKeyState: true);

            Assert.True(assignment.Assigned);
            Assert.True(assignment.Keyed);
            Assert.True(assignment.HealthStarted);
            Assert.True(sockets.Connected);
        }

        [Fact]
        public void Requires_assignment()
        {
            Assert.Throws<System.ArgumentNullException>(() => new ConnectionOpenHost(null));
        }

        private sealed class ForwardingSockets : IConnectionSocketSessionFactory
        {
            public bool Connected { get; private set; }

            public object CreateAndWire(ConnectionSocketSessionRequest request) => new object();

            public Task<object> InitializeKeyStoreAsync(object session) =>
                Task.FromResult<object>("keys");

            public Task ConnectTransportAsync(object session)
            {
                Connected = true;
                return Task.CompletedTask;
            }

            public void DisposeSession(object session)
            {
            }
        }

        private sealed class FakeAssignment : IConnectionSocketAssignment
        {
            public bool Assigned { get; private set; }
            public bool Keyed { get; private set; }
            public bool HealthStarted { get; private set; }

            public bool ShouldRefuseConnectBecauseSessionDied => false;

            public ConnectionSocketSessionRequest CreateSocketSessionRequest(bool reuseLoadedKeyState) =>
                new ConnectionSocketSessionRequest
                {
                    ReuseLoadedKeyState = reuseLoadedKeyState,
                    WireOwner = this
                };

            public void AssignSocketSession(object session) => Assigned = session != null;

            public void ApplyPersistentKeyStore(object keyStore, bool reuseLoadedKeyState) =>
                Keyed = keyStore != null;

            public void AbortAssignedSocket(string journalReason)
            {
            }

            public void StartHealthMonitorIfAssignedCurrent() => HealthStarted = true;

            public void ClearAssignedSocketAfterFailedConnect()
            {
            }
        }
    }
}
