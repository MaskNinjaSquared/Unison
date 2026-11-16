// =============================================================================
// ISilentFullHistoryChainHost
//
// Side effects for SilentFullHistoryChainDispatch.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ISilentFullHistoryChainHost
    {
        bool IsSilentChainArmed { get; }

        bool IsPreviousSilentLotQuiet { get; }

        /// <summary>Consecutive silent quiet lots that applied zero messages (session).</summary>
        int ConsecutiveEmptySilentLots { get; }

        bool IsSocketReadyForSilentFull { get; }

        bool IsHistoryCatchUpActive { get; }

        bool IsFullHistoryClaimed { get; }

        /// <summary>True when tip freshness still looks behind the watermark.</summary>
        bool IsHistoryTipStale { get; }

        /// <summary>Gap before the next silent lot (product knob).</summary>
        TimeSpan SilentLotDelay { get; }

        void MarkSilentChainArmed(bool armed);

        void MarkPreviousSilentLotQuiet(bool quiet);

        void MarkConsecutiveEmptySilentLots(int count);

        void CancelSilentSchedule();

        CancellationTokenSource CreateSilentScheduleCts();

        void StartSilentScheduleTask(Func<Task> work);

        Task DelayAsync(TimeSpan delay, CancellationToken token);

        Task<bool> RequestSilentFullHistoryAsync(string reason);

        void BeginHistoryCatchUpBanner(string reason);

        void EndHistoryCatchUpBanner(string reason);

        void WriteSilentJournal(string eventName, string detail);

        void LogSilentChainFailed(string message);
    }
}
