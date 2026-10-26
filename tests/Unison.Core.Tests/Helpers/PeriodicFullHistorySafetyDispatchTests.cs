using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PeriodicFullHistorySafetyDispatchTests
    {
        [Fact]
        public async Task Skips_when_socket_not_ready()
        {
            var host = new FakeHost { SocketReady = false, TipStale = true };
            await PeriodicFullHistorySafetyDispatch.RunAsync(host);
            Assert.False(host.Requested);
        }

        [Fact]
        public async Task Skips_when_no_debt()
        {
            var host = new FakeHost
            {
                SocketReady = true,
                TipStale = false,
                ForceRepair = false
            };
            await PeriodicFullHistorySafetyDispatch.RunAsync(host);
            Assert.False(host.Requested);
        }

        [Fact]
        public async Task Solicites_when_tip_stale()
        {
            var host = new FakeHost
            {
                SocketReady = true,
                TipStale = true,
                StaleDetail = "tip-old"
            };
            await PeriodicFullHistorySafetyDispatch.RunAsync(host);
            Assert.True(host.Requested);
            Assert.Contains("periodic-safety:tip-stale:tip-old", host.RequestReason);
            Assert.True(host.FreshnessRepair);
        }

        [Fact]
        public async Task Skips_when_cascade_disabled_even_if_tip_stale()
        {
            var host = new FakeHost
            {
                SocketReady = true,
                TipStale = true,
                StaleDetail = "tip-old",
                AllowCascade = false
            };
            await PeriodicFullHistorySafetyDispatch.RunAsync(host);
            Assert.False(host.Requested);
        }

        private sealed class FakeHost : IPeriodicFullHistorySafetyHost
        {
            public bool SocketReady;
            public bool TipStale;
            public bool ForceRepair;
            public bool AllowCascade = true;
            public string StaleDetail;
            public bool Requested;
            public string RequestReason;
            public bool FreshnessRepair;

            public bool IsSocketReadyForPeriodicFullSafety => SocketReady;
            public bool IsHistoryCatchUpActive => false;
            public bool IsFullHistoryClaimed => false;
            public bool AllowStagnantHistoryRepairCascade => AllowCascade;

            public bool TryReadForceHistoryRepairPending() => ForceRepair;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleDetail)
            {
                staleDetail = TipStale ? StaleDetail : null;
                return TipStale;
            }

            public void LogPeriodicFullSafety(string message)
            {
            }

            public void WritePeriodicFullSafetyJournal(string reason)
            {
            }

            public Task<bool> RequestFullHistoryOnDemandTrackedAsync(
                string reason,
                bool isFreshnessRepair)
            {
                Requested = true;
                RequestReason = reason;
                FreshnessRepair = isFreshnessRepair;
                return Task.FromResult(true);
            }
        }
    }
}
