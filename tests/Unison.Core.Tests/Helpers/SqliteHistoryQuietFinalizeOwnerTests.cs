using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.History;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SqliteHistoryQuietFinalizeOwnerTests
    {
        [Fact]
        public async Task Stale_generation_skips_dispatch()
        {
            var io = new FakeIo();
            var owner = new SqliteHistoryQuietFinalize(io);
            int gen = owner.NextGeneration();
            owner.BumpGeneration();

            owner.ScheduleAfterQuiet(gen, delayMs: 0, processedAtSchedule: 3);
            await Task.Delay(350);

            Assert.False(io.DispatchRan);
            Assert.Equal(2, owner.Generation);
        }

        [Fact]
        public async Task Matching_generation_runs_dispatch()
        {
            var io = new FakeIo();
            var owner = new SqliteHistoryQuietFinalize(io);
            int gen = owner.NextGeneration();

            owner.ScheduleAfterQuiet(gen, delayMs: 0, processedAtSchedule: 3);
            await Task.Delay(350);

            Assert.True(io.DispatchRan);
            Assert.Equal(0, io.Accumulated);
        }

        private sealed class FakeIo : ISqliteHistoryQuietFinalizeHost
        {
            public bool DispatchRan { get; private set; }
            public int Accumulated { get; set; } = 3;

            public int SqliteHistoryConversationsAccumulated
            {
                get => Accumulated;
                set => Accumulated = value;
            }

            public bool FirstSyncListReleaseDeferred { get; set; }
            public bool IsHistoryCatchUpActive => false;
            public bool IsHistoryCatchUpBannerActive => false;
            public bool EnrichmentAwaitingHeavyHistory => false;
            public bool IsFirstSyncListHoldInProgress => false;
            public bool IsListPaintPending => false;

            public bool TryHandOffQuietLot(
                bool catchUpBanner,
                out string lotRequestId,
                out HistoryOnDemandRequest lotRequest)
            {
                lotRequestId = null;
                lotRequest = null;
                return false;
            }

            public void PublishInitialSyncProgress(
                bool isActive,
                bool isCompleted,
                int processed,
                int total,
                string reason)
            {
                DispatchRan = true;
            }

            public void RequestFirstSyncListReleaseAfterNames(
                string reason,
                int processed,
                int total)
            {
            }

            public void NotifyCatchUpIdleBatch(
                string lotRequestId,
                bool lotAckAccepted,
                int lotProgressSignals,
                string triggerDetail,
                int delayMs)
            {
            }

            public void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment)
            {
            }

            public void ClearFullHistoryOnDemandRequestState(string reason)
            {
            }

            public void EndHistoryCatchUpBanner(string reason)
            {
            }

            public void WriteCatchUpSqliteLotQuiet(
                string lotRequestId,
                int lotProgressSignals,
                int delayMs,
                int processed,
                bool deferListRelease)
            {
            }

            public void WriteCatchUpSqliteLotQuietSkipEmpty(
                string lotRequestId,
                bool firstSyncHold)
            {
            }

            public void WriteCatchUpSqliteLotQuietKeepHold(string lotRequestId)
            {
            }

            public void WritePreviewReconcileDeferredListPaint()
            {
            }

            public void WritePreviewReconcileDeferredCatchUp()
            {
            }

            public Task ReconcileChatPreviewsFromSqliteAsync() => Task.CompletedTask;

            public void LogPreviewReconcileFailed(System.Exception ex)
            {
            }

            public void LogFinalizeFailed(System.Exception ex)
            {
            }

            public void LogQuietLotHandOff(
                string lotRequestId,
                int lotProgressSignals,
                int processed,
                bool bannerActive,
                bool deferListRelease)
            {
            }
        }
    }
}
