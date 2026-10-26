using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionResumeTests
    {
        [Fact]
        public async Task Skips_when_not_registered()
        {
            var host = new FakeHost { Registered = false };
            await ConnectionResume.RunAsync(host);
            Assert.Equal(0, host.Ensures);
        }

        [Fact]
        public async Task Blocks_when_fatal_session_ended()
        {
            var host = new FakeHost { Registered = true, Fatal = true };
            await ConnectionResume.RunAsync(host);
            Assert.Equal(0, host.Ensures);
            Assert.Equal("resume-blocked", host.LastJournal);
        }

        [Fact]
        public async Task Stops_after_successful_broker_reclaim()
        {
            var host = new FakeHost { Registered = true, ReclaimSucceeds = true };
            await ConnectionResume.RunAsync(host);
            Assert.Equal(0, host.Ensures);
            Assert.Equal(1, host.Reclaims);
        }

        [Fact]
        public async Task Ensures_then_restarts_pump_on_normal_resume()
        {
            var host = new FakeHost { Registered = true };
            await ConnectionResume.RunAsync(host);
            Assert.Equal(1, host.Ensures);
            Assert.Equal(1, host.PumpRestarts);
            Assert.Equal(0, host.SoftReconnects);
        }

        [Fact]
        public async Task Soft_reconnects_when_ensure_throws()
        {
            var host = new FakeHost { Registered = true, EnsureThrows = true };
            await ConnectionResume.RunAsync(host);
            Assert.Equal(1, host.SoftReconnects);
            Assert.Equal("resume-fallback", host.LastSoftReason);
        }

        private sealed class FakeHost : IConnectionResumeHost
        {
            public bool Registered { get; set; }
            public bool Fatal { get; set; }
            public bool ReclaimSucceeds { get; set; }
            public bool EnsureThrows { get; set; }
            public int Ensures { get; private set; }
            public int Reclaims { get; private set; }
            public int PumpRestarts { get; private set; }
            public int SoftReconnects { get; private set; }
            public string LastJournal { get; private set; } = string.Empty;
            public string LastSoftReason { get; private set; } = string.Empty;
            private bool _forceFresh;

            public bool IsFatalSessionEnded => Fatal;

            public Task<bool> IsRegisteredAsync() => Task.FromResult(Registered);

            public void ClearSuppressReconnect()
            {
            }

            public Task<bool> ConsumeBrokerReconnectRequestAsync() => Task.FromResult(false);

            public void MarkForceFreshConnectOnResume() => _forceFresh = true;

            public Task<bool> TryReclaimBrokerOwnedSocketAsync()
            {
                Reclaims++;
                return Task.FromResult(ReclaimSucceeds);
            }

            public bool ConsumeForceFreshConnectOnResume()
            {
                bool value = _forceFresh;
                _forceFresh = false;
                return value;
            }

            public Task EnsureConnectedAsync(int timeoutMs, bool forceFreshTransport)
            {
                Ensures++;
                if (EnsureThrows)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.CompletedTask;
            }

            public void RestartIncomingMessagePumpIfNeeded() => PumpRestarts++;

            public void RequestSoftReconnect(string reason)
            {
                SoftReconnects++;
                LastSoftReason = reason;
            }

            public void WriteResumeJournal(string eventName, string detail) => LastJournal = eventName;

            public void RecordResumeException(string eventName, Exception error)
            {
            }
        }
    }
}
