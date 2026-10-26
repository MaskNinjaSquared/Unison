using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PostReplayMaintenanceDispatchTests
    {
        [Fact]
        public void Delayed_bulk_only_above_threshold_without_frugal()
        {
            Assert.True(PostReplayMaintenanceGates.ShouldRunDelayedBulk(50, false));
            Assert.False(PostReplayMaintenanceGates.ShouldRunDelayedBulk(49, false));
            Assert.False(PostReplayMaintenanceGates.ShouldRunDelayedBulk(50, true));
        }

        [Fact]
        public void Abort_when_quiet_or_connection_or_window_fails()
        {
            Assert.True(PostReplayMaintenanceGates.ShouldAbortBeforeWork(false, true, true));
            Assert.True(PostReplayMaintenanceGates.ShouldAbortBeforeWork(true, false, true));
            Assert.True(PostReplayMaintenanceGates.ShouldAbortBeforeWork(true, true, false));
            Assert.False(PostReplayMaintenanceGates.ShouldAbortBeforeWork(true, true, true));
        }

        [Fact]
        public async Task Quiet_abort_clears_status_and_skips_notify()
        {
            var host = new FakeHost { Quiet = false };

            await PostReplayMaintenanceDispatch.RunAsync(
                host,
                offlineCount: 12,
                CancellationToken.None);

            Assert.Equal(new[] { "clear-status" }, host.Log);
        }

        [Fact]
        public async Task Memory_skip_clears_status()
        {
            var host = new FakeHost { MemoryOk = false };

            await PostReplayMaintenanceDispatch.RunAsync(
                host,
                offlineCount: 12,
                CancellationToken.None);

            Assert.Equal(
                new[] { "memory-skip", "clear-status" },
                host.Log);
        }

        [Fact]
        public async Task Catch_up_defers_without_notify()
        {
            var host = new FakeHost { CatchUpActive = true };

            await PostReplayMaintenanceDispatch.RunAsync(
                host,
                offlineCount: 12,
                CancellationToken.None);

            Assert.Contains(host.Log, line => line.StartsWith("deferred:"));
            Assert.Contains("enrichment-await-heavy", host.Log);
            Assert.DoesNotContain(host.Log, line => line.StartsWith("notify:"));
        }

        [Fact]
        public async Task List_paint_pending_skips_notify_but_arms_enrichment()
        {
            var host = new FakeHost { ListPaintPending = true };

            await PostReplayMaintenanceDispatch.RunAsync(
                host,
                offlineCount: 12,
                CancellationToken.None);

            Assert.Equal(
                new[]
                {
                    "list-paint-deferred",
                    "complete:12",
                    "enrich:post-replay:12"
                },
                host.Log);
        }

        [Fact]
        public async Task Happy_path_notifies_with_delayed_bulk()
        {
            var host = new FakeHost { UseFrugal = false };

            await PostReplayMaintenanceDispatch.RunAsync(
                host,
                offlineCount: 50,
                CancellationToken.None);

            Assert.Equal(
                new[]
                {
                    "notify:50:True",
                    "complete:50",
                    "enrich:post-replay:50"
                },
                host.Log);
        }

        private sealed class FakeHost : IPostReplayMaintenanceHost
        {
            public List<string> Log { get; } = new List<string>();

            public bool Quiet { get; set; } = true;

            public bool MemoryOk { get; set; } = true;

            public bool UseFrugal { get; set; }

            public bool CatchUpActive { get; set; }

            public bool ListPaintPending { get; set; }

            public bool UseFrugalSyncBudget => UseFrugal;

            public bool IsConnected => true;

            public bool IsWindowVisible => true;

            public bool IsHistoryCatchUpActive => CatchUpActive;

            public bool IsListPaintPending => ListPaintPending;

            public Task<bool> WaitForStartupQuietAsync(
                TimeSpan timeout,
                CancellationToken token) =>
                Task.FromResult(Quiet);

            public Task<bool> WaitForMemoryHeadroomAsync(CancellationToken token) =>
                Task.FromResult(MemoryOk);

            public void ClearSyncStatus() => Log.Add("clear-status");

            public void WriteMemorySkipped() => Log.Add("memory-skip");

            public void MarkEnrichmentAwaitingHeavyHistory() =>
                Log.Add("enrichment-await-heavy");

            public void WriteDeferred(string catchUpDeferReason, int offlineCount) =>
                Log.Add("deferred:" + catchUpDeferReason + ":" + offlineCount);

            public void WriteListPaintDeferred() => Log.Add("list-paint-deferred");

            public Task NotifyPostReplayMaintenanceAsync(
                int offlineCount,
                bool runDelayedBulk,
                CancellationToken token)
            {
                Log.Add("notify:" + offlineCount + ":" + runDelayedBulk);
                return Task.CompletedTask;
            }

            public void WriteComplete(int offlineCount) =>
                Log.Add("complete:" + offlineCount);

            public void TrySchedulePostMessageEnrichment(string reason) =>
                Log.Add("enrich:" + reason);

            public void RecordFailed(Exception ex, int offlineCount) =>
                Log.Add("failed:" + offlineCount);
        }
    }
}