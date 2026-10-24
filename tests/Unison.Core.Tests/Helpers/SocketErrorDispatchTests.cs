using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SocketErrorDispatchTests
    {
        [Fact]
        public void Stream_stale_non_logout_is_ignored()
        {
            var host = new FakeHost();
            SocketErrorDispatch.HandleStreamError(host, "440", isCurrentSocket: false);
            Assert.Empty(host.Log);
        }

        [Fact]
        public void Stream_515_on_current_claims_pairing_stage2()
        {
            var host = new FakeHost();
            SocketErrorDispatch.HandleStreamError(host, "515", isCurrentSocket: true);

            Assert.Equal(
                new[]
                {
                    "pairing-pending:stream-error-515",
                    "stage2:stream-error-515",
                    "report-stream:515"
                },
                host.Log);
        }

        [Fact]
        public void Stream_401_latches_fatal_even_when_stale()
        {
            var host = new FakeHost();
            SocketErrorDispatch.HandleStreamError(host, "401", isCurrentSocket: false);

            Assert.Equal(
                new[] { "fatal:stream-401", "report-stream:401" },
                host.Log);
        }

        [Fact]
        public async Task Error_transport_failure_schedules_reconnect()
        {
            var host = new FakeHost();
            var ex = new Exception("connection was aborted");

            await SocketErrorDispatch.HandleErrorAsync(
                host,
                ex,
                isCurrentSocket: true,
                socketConnected: false,
                handshakeComplete: false);

            Assert.Contains(host.Log, line => line.StartsWith("log-error:"));
            Assert.Contains("transport-scheduled", host.Log);
            Assert.Contains("delay:250", host.Log);
            Assert.Contains("reconnect:socket-error", host.Log);
            Assert.Contains("raise-error", host.Log);
        }

        [Fact]
        public async Task Error_during_pairing_defers_reconnect()
        {
            var host = new FakeHost { PairingRestartPending = true };
            var ex = new Exception("connection was aborted");

            await SocketErrorDispatch.HandleErrorAsync(
                host,
                ex,
                isCurrentSocket: true,
                socketConnected: false,
                handshakeComplete: false);

            Assert.Contains(
                "trace:transport failure during pairing stage2 → defer to ReconnectForPairingAsync",
                host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("reconnect:"));
            Assert.Contains("raise-error", host.Log);
        }

        [Fact]
        public async Task Error_when_suppress_reconnect_only_raises()
        {
            var host = new FakeHost { SuppressReconnect = true };
            var ex = new Exception("boom");

            await SocketErrorDispatch.HandleErrorAsync(
                host,
                ex,
                isCurrentSocket: true,
                socketConnected: true,
                handshakeComplete: true);

            Assert.Contains("raise-error", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("reconnect:"));
        }

        private sealed class FakeHost : ISocketErrorHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool SuppressReconnect { get; set; }

            public bool FatalSessionEnded { get; set; }

            public bool PairingRestartPending { get; set; }

            public void MarkPairingRestartPending(string reason) =>
                Log.Add("pairing-pending:" + reason);

            public void TryStartPairingStage2Reconnect(string trigger) =>
                Log.Add("stage2:" + trigger);

            public void LatchFatalSession(string reason) =>
                Log.Add("fatal:" + reason);

            public void ReportStreamError(string code) =>
                Log.Add("report-stream:" + code);

            public void LogSocketError(Exception ex, bool socketConnected, bool handshakeComplete) =>
                Log.Add("log-error:" + ex.Message);

            public void PairingTrace(string line) =>
                Log.Add("trace:" + line);

            public void LogTransportFailureScheduled() =>
                Log.Add("transport-scheduled");

            public Task DelayBeforeReconnectAsync(int milliseconds)
            {
                Log.Add("delay:" + milliseconds);
                return Task.CompletedTask;
            }

            public void ScheduleAutoReconnect(string trigger) =>
                Log.Add("reconnect:" + trigger);

            public void RaiseOnError(Exception ex) =>
                Log.Add("raise-error");
        }
    }
}
