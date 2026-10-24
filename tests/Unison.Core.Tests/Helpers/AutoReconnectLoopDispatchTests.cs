using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AutoReconnectLoopDispatchTests
    {
        [Fact]
        public async Task Exits_when_reconnect_suppressed()
        {
            var host = new FakeHost { Suppress = true };
            await AutoReconnectLoopDispatch.RunAsync(host, "t");
            Assert.Equal(0, host.ConnectCalls);
        }

        [Fact]
        public async Task Exits_when_socket_already_healthy()
        {
            var host = new FakeHost { Healthy = true };
            await AutoReconnectLoopDispatch.RunAsync(host, "t");
            Assert.Equal(0, host.ConnectCalls);
            Assert.Contains("healthy socket", host.LastLog);
        }

        [Fact]
        public async Task Stops_when_session_not_registered()
        {
            var host = new FakeHost { AuthPresent = true, Registered = false };
            await AutoReconnectLoopDispatch.RunAsync(host, "t");
            Assert.Equal(0, host.ConnectCalls);
            Assert.Contains("not registered", host.LastLog);
        }

        private sealed class FakeHost : IAutoReconnectLoopHost
        {
            public bool Suppress;
            public bool Healthy;
            public bool AuthPresent = true;
            public bool Registered = true;
            public int ConnectCalls;
            public string LastLog;

            public bool SuppressReconnect => Suppress;
            public bool FatalSessionEnded => false;
            public bool PairingRestartPending => false;
            public bool IsAuthStatePresent => AuthPresent;
            public bool IsAuthRegistered => Registered;

            public Task<bool> IsCurrentSocketHealthyAsync() => Task.FromResult(Healthy);

            public Task InitializeAsync() => Task.CompletedTask;

            public Task ConnectAsync()
            {
                ConnectCalls++;
                return Task.CompletedTask;
            }

            public TimeSpan ReconnectDelayForAttempt(int attempt) => TimeSpan.Zero;

            public void PublishReconnectingStatus()
            {
            }

            public void LogAutoReconnect(string message) => LastLog = message;

            public void TracePairing(string detail)
            {
            }

            public void RaiseReconnectLoopError(Exception error)
            {
            }
        }
    }
}
