// =============================================================================
// ICatchUpContinueReconnectHost
//
// Side effects for CatchUpContinueReconnectDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ICatchUpContinueReconnectHost
    {
        bool FreshnessReconnectFallbackInProgress { get; set; }

        bool SuppressReconnect { get; }

        bool IsAuthRegistered { get; }

        DateTime LastCatchUpContinueUtc { get; set; }

        TimeSpan CatchUpContinueReconnectCooldown { get; }

        TimeSpan CatchUpContinueReconnectDelay { get; }

        int CatchUpRound { get; }

        bool IsHistoryCatchUpBannerActive { get; }

        bool IsHistoryCatchUpActive();

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        void FinishHistoryCatchUpContinue(string reason, bool releaseEnrichment);

        void MarkEnrichmentAwaitingHeavyHistory();

        void BeginHistoryCatchUpBanner(string reason);

        void PublishHistoryCatchUpStatus();

        void WriteCatchUpContinueJournal(string triggerReason, int delayMs);

        void LogCatchUpContinue(string message);

        void StopConnectionHealthMonitor(string reason);

        void TearDownSocketForReconnect();

        void ScheduleAutoReconnect(string reason);

        void RecordCatchUpContinueException(Exception error, string triggerReason);

        void FireAndForgetCatchUpContinue(Func<Task> work);
    }
}
