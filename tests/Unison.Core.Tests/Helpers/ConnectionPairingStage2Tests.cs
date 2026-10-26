using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionPairingStage2Tests
    {
        [Fact]
        public async Task Aborts_when_suppress_or_fatal()
        {
            var host = new FakeHost { Abort = true, PairingPending = true, Reconnecting = true };
            await ConnectionPairingStage2.RunAsync(host);
            Assert.False(host.PairingPending);
            Assert.False(host.Reconnecting);
            Assert.Equal(0, host.Connects);
            Assert.Contains("aborted", host.LastTrace);
        }

        [Fact]
        public async Task Connects_after_stage1_wait()
        {
            var host = new FakeHost { Status = "open" };
            await ConnectionPairingStage2.RunAsync(host);
            Assert.Equal(1, host.Connects);
            Assert.False(host.Reconnecting);
            Assert.Equal(0, host.SoftReconnects);
        }

        [Fact]
        public async Task Soft_reconnects_when_connect_fails_and_registered()
        {
            var host = new FakeHost
            {
                Registered = true,
                CanSoft = true,
                ConnectThrows = true
            };
            await ConnectionPairingStage2.RunAsync(host);
            Assert.Equal(1, host.SoftReconnects);
            Assert.Equal("pairing-stage2-failed", host.LastSoftReason);
            Assert.False(host.PairingPending);
            Assert.NotNull(host.RaisedError);
        }

        [Fact]
        public async Task Does_not_soft_reconnect_when_unregistered_after_failure()
        {
            var host = new FakeHost { Registered = false, CanSoft = true, ConnectThrows = true };
            await ConnectionPairingStage2.RunAsync(host);
            Assert.Equal(0, host.SoftReconnects);
        }

        private sealed class FakeHost : IConnectionPairingStage2Host
        {
            public bool Abort { get; set; }
            public bool Registered { get; set; }
            public bool CanSoft { get; set; }
            public bool ConnectThrows { get; set; }
            public bool PairingPending { get; set; } = true;
            public bool Reconnecting { get; set; } = true;
            public int Connects { get; private set; }
            public int SoftReconnects { get; private set; }
            public string LastTrace { get; private set; } = string.Empty;
            public string LastSoftReason { get; private set; } = string.Empty;
            public Exception RaisedError { get; private set; }
            public string Status { get; set; } = string.Empty;

            public bool ShouldAbortPairingRestart => Abort;
            public bool IsRegistered => Registered;
            public bool CanScheduleSoftReconnect => CanSoft;
            public string CurrentConnectionStatusOrNull => Status;

            public void ClearPairingRestartPending() => PairingPending = false;

            public void ClearReconnectingFlag() => Reconnecting = false;

            public Task ConnectAsync()
            {
                Connects++;
                if (ConnectThrows)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.CompletedTask;
            }

            public void RequestSoftReconnect(string reason)
            {
                SoftReconnects++;
                LastSoftReason = reason;
            }

            public void RaisePairingError(Exception error) => RaisedError = error;

            public void TracePairing(string detail) => LastTrace = detail;

            public void LogPairingReset()
            {
            }
        }
    }
}
