using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SqliteHistoryQuietFinalizeDispatchTests
    {
        [Fact]
        public async Task Completes_initial_sync_when_no_catch_up_hand_off()
        {
            var host = new FakeHost
            {
                Accumulated = 4,
                HandOff = false,
                FirstSyncHold = false
            };

            await SqliteHistoryQuietFinalizeDispatch.RunAsync(host, 500, 4);

            Assert.Equal("sqlite-finalized", host.LastPublishReason);
            Assert.False(host.LastPublishActive);
            Assert.True(host.LastPublishCompleted);
            Assert.Equal(0, host.Accumulated);
            Assert.True(host.ClearedFullHistory);
            Assert.True(host.EndedBanner);
        }

        [Fact]
        public async Task Defers_list_release_when_first_sync_and_hand_off()
        {
            var registry = new HistoryOnDemandRegistry();
            Assert.True(registry.TryClaimFull());
            HistoryOnDemandRequest lot = registry.RegisterFull(
                "lot-1",
                isRepair: true,
                baselineMessageCount: 10,
                reason: "full",
                nowUtc: DateTime.UtcNow);
            registry.Ack("lot-1", error: null, DateTime.UtcNow);

            var host = new FakeHost
            {
                Accumulated = 2,
                HandOff = true,
                FirstSyncHold = true,
                CatchUpActive = true,
                LotRequest = lot
            };

            await SqliteHistoryQuietFinalizeDispatch.RunAsync(host, 800, 2);

            Assert.Equal("sqlite-quiet-catch-up", host.LastPublishReason);
            Assert.True(host.FirstSyncDeferred);
            Assert.True(host.NotifiedIdleBatch);
        }

        private sealed class FakeHost : ISqliteHistoryQuietFinalizeHost
        {
            public int Accumulated { get; set; }
            public bool HandOff { get; set; }
            public bool FirstSyncHold { get; set; }
            public bool CatchUpActive { get; set; }
            public HistoryOnDemandRequest LotRequest { get; set; }
            public string LastPublishReason { get; private set; }
            public bool LastPublishActive { get; private set; }
            public bool LastPublishCompleted { get; private set; }
            public bool FirstSyncDeferred { get; private set; }
            public bool ClearedFullHistory { get; private set; }
            public bool EndedBanner { get; private set; }
            public bool NotifiedIdleBatch { get; private set; }

            public int SqliteHistoryConversationsAccumulated
            {
                get => Accumulated;
                set => Accumulated = value;
            }

            public bool FirstSyncListReleaseDeferred
            {
                get => FirstSyncDeferred;
                set => FirstSyncDeferred = value;
            }

            public bool IsHistoryCatchUpActive => CatchUpActive;
            public bool IsHistoryCatchUpBannerActive => CatchUpActive;
            public bool EnrichmentAwaitingHeavyHistory => false;
            public bool IsFirstSyncListHoldInProgress => FirstSyncHold;
            public bool IsListPaintPending => false;

            public bool TryHandOffQuietLot(
                bool catchUpBanner,
                out string lotRequestId,
                out HistoryOnDemandRequest lotRequest)
            {
                lotRequestId = HandOff ? "lot-1" : null;
                lotRequest = HandOff ? LotRequest : null;
                return HandOff;
            }

            public void PublishInitialSyncProgress(
                bool isActive,
                bool isCompleted,
                int processed,
                int total,
                string reason)
            {
                LastPublishActive = isActive;
                LastPublishCompleted = isCompleted;
                LastPublishReason = reason;
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
                int delayMs) =>
                NotifiedIdleBatch = true;

            public void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment)
            {
            }

            public void ClearFullHistoryOnDemandRequestState(string reason) =>
                ClearedFullHistory = true;

            public void EndHistoryCatchUpBanner(string reason) => EndedBanner = true;

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

            public void LogPreviewReconcileFailed(Exception ex)
            {
            }

            public void LogFinalizeFailed(Exception ex)
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
