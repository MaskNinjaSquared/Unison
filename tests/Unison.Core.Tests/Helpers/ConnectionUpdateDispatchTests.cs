using System.Collections.Generic;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionUpdateDispatchTests
    {
        [Fact]
        public void Shutdown_publishes_and_skips_close_policy()
        {
            var host = new FakeHost { SuppressReconnect = true };

            ConnectionUpdateDispatch.Handle(host, "close");

            Assert.Equal(
                new[] { "ignored:close", "publish:close" },
                host.Log);
        }

        [Fact]
        public void Restart_claims_stage2_then_publishes()
        {
            var host = new FakeHost { IsAuthRegistered = true };

            ConnectionUpdateDispatch.Handle(host, "restart");

            Assert.Equal(
                new[]
                {
                    "diag-pairing-restart",
                    "trace:connection status=restart → stage2",
                    "stage2:connection-update-restart",
                    "publish:restart"
                },
                host.Log);
        }

        [Fact]
        public void Registered_close_while_pairing_pending_publishes_early()
        {
            var host = new FakeHost
            {
                IsAuthRegistered = true,
                PairingRestartPending = true
            };

            ConnectionUpdateDispatch.Handle(host, "close");

            Assert.Equal(
                new[]
                {
                    "stop-health:socket-close",
                    "diag-pairing-restart-close-ignored",
                    "trace:close while pairingRestartPending → SKIP ScheduleAutoReconnect (stage2 owner)",
                    "publish:close"
                },
                host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("reconnect:"));
        }

        [Fact]
        public void Registered_close_after_session_soft_reconnects()
        {
            var host = new FakeHost
            {
                IsAuthRegistered = true,
                SessionEstablishedThisConnection = true
            };

            ConnectionUpdateDispatch.Handle(host, "close");

            Assert.Contains("stop-health:socket-close", host.Log);
            Assert.Contains("reset-streak", host.Log);
            Assert.Contains("reconnect:socket-close", host.Log);
            Assert.Contains("publish:close", host.Log);
        }

        [Fact]
        public void Unregistered_qr_close_raises_expired()
        {
            var host = new FakeHost
            {
                IsAuthRegistered = false,
                QrDeliveredThisConnection = true,
                PairingRestartPending = false
            };

            ConnectionUpdateDispatch.Handle(host, "close");

            Assert.Contains("qr-expired:socket-close", host.Log);
            Assert.Contains("raise-qr-expired", host.Log);
            Assert.Contains("publish:close", host.Log);
        }

        [Fact]
        public void Pre_session_streak_fatal_skips_reconnect_when_host_marks_fatal()
        {
            var host = new FakeHost
            {
                IsAuthRegistered = true,
                SessionEstablishedThisConnection = false,
                CountPreSessionCloseAsFatal = true,
                PreSessionCloseFatalThreshold = 1,
                MarkFatalOnInvalidSession = true
            };

            ConnectionUpdateDispatch.Handle(host, "close");

            Assert.Contains("invalid-session:pre-session-close-streak", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("reconnect:"));
            Assert.Contains("publish:close", host.Log);
        }

        private sealed class FakeHost : IConnectionUpdateHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool SuppressReconnect { get; set; }

            public bool FatalSessionEnded { get; set; }

            public bool IsAuthRegistered { get; set; }

            public bool PairingRestartPending { get; set; }

            public bool SessionEstablishedThisConnection { get; set; }

            public bool CountPreSessionCloseAsFatal { get; set; }

            public bool QrDeliveredThisConnection { get; set; }

            public int PreSessionCloseFatalThreshold { get; set; } = 3;

            public bool MarkFatalOnInvalidSession { get; set; }

            private int _streak;

            public void LogIgnoredDuringShutdown(string status) =>
                Log.Add("ignored:" + status);

            public void PublishConnectionUpdate(string status) =>
                Log.Add("publish:" + status);

            public void WritePairingRestartDiagnostics() =>
                Log.Add("diag-pairing-restart");

            public void PairingTrace(string line) =>
                Log.Add("trace:" + line);

            public void StopConnectionHealthMonitor(string reason) =>
                Log.Add("stop-health:" + reason);

            public void WritePairingRestartCloseIgnored() =>
                Log.Add("diag-pairing-restart-close-ignored");

            public void WritePairingCloseBeforeRestart() =>
                Log.Add("diag-pairing-close-before-restart");

            public void TryStartPairingStage2Reconnect(string trigger) =>
                Log.Add("stage2:" + trigger);

            public int IncrementPreSessionCloseStreak() => ++_streak;

            public void WritePreSessionClose(int streak) =>
                Log.Add("pre-session:" + streak);

            public void RaiseInvalidSessionSuspected(string reason)
            {
                Log.Add("invalid-session:" + reason);
                if (MarkFatalOnInvalidSession)
                {
                    FatalSessionEnded = true;
                }
            }

            public void ResetPreSessionCloseStreak() =>
                Log.Add("reset-streak");

            public void ScheduleAutoReconnect(string trigger) =>
                Log.Add("reconnect:" + trigger);

            public void WriteQrExpired(string trigger) =>
                Log.Add("qr-expired:" + trigger);

            public void RaiseQrExpired() =>
                Log.Add("raise-qr-expired");
        }
    }
}
