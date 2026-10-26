using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionEnsureTests
    {
        [Fact]
        public async Task Returns_immediately_when_socket_is_already_healthy()
        {
            var host = new FakeHost { Healthy = true };
            host.MarkConnected();
            var gate = new SemaphoreSlim(1, 1);

            await ConnectionEnsure.RunAsync(host, gate, 1000, forceFreshTransport: false);

            Assert.Equal(0, host.Connects);
            Assert.Equal(0, host.Drops);
        }

        [Fact]
        public async Task Connects_when_unhealthy_and_registered()
        {
            var host = new FakeHost { Healthy = false, Registered = true, ConnectedAfterConnect = true };
            var gate = new SemaphoreSlim(1, 1);

            await ConnectionEnsure.RunAsync(host, gate, 2000, forceFreshTransport: false);

            Assert.Equal(1, host.Connects);
        }

        [Fact]
        public async Task Drops_socket_when_force_fresh()
        {
            var host = new FakeHost { Healthy = true, Registered = true, ConnectedAfterConnect = true };
            var gate = new SemaphoreSlim(1, 1);

            await ConnectionEnsure.RunAsync(host, gate, 2000, forceFreshTransport: true);

            Assert.Equal(1, host.Drops);
            Assert.Equal(1, host.Connects);
        }

        [Fact]
        public async Task Throws_when_not_registered()
        {
            var host = new FakeHost { Healthy = false, Registered = false };
            var gate = new SemaphoreSlim(1, 1);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ConnectionEnsure.RunAsync(host, gate, 1000, forceFreshTransport: false));
        }

        private sealed class FakeHost : IConnectionEnsureHost
        {
            public bool Healthy { get; set; }
            public bool Registered { get; set; }
            public bool ConnectedAfterConnect { get; set; }
            public int Connects { get; private set; }
            public int Drops { get; private set; }

            public bool IsConnected { get; private set; }

            public void MarkConnected()
            {
                IsConnected = true;
            }

            public Task EnsureConnectedAsync(int timeoutMs = 35000, bool forceFreshTransport = false) =>
                ConnectionEnsure.RunAsync(this, new SemaphoreSlim(1, 1), timeoutMs, forceFreshTransport);

            public Task<bool> IsCurrentSocketHealthyAsync() => Task.FromResult(Healthy);

            public void DropCurrentSocketForFastResume()
            {
                Drops++;
                Healthy = false;
                IsConnected = false;
            }

            public Task<bool> IsRegisteredAsync() => Task.FromResult(Registered);

            public Task ConnectAsync()
            {
                Connects++;
                IsConnected = ConnectedAfterConnect;
                Healthy = ConnectedAfterConnect;
                return Task.CompletedTask;
            }

            public Task WaitForSessionEstablishedAsync(int timeoutMs) => Task.CompletedTask;
        }
    }
}
