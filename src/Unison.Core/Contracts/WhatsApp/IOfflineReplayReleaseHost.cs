using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Side effects <c>OfflineReplayRelease</c> needs when the socket reports
    /// pending-notification drain complete.
    /// </summary>
    public interface IOfflineReplayReleaseHost
    {
        /// <summary>True the first time this process marks the offline release.</summary>
        bool TryMarkFirstRelease();

        void WriteReleaseDiagnostics(int offlineCount, bool firstRelease);

        void ClearReplayDrainPending();

        Task FlushOfflineReplayMessagesAsync(string reason);

        bool IsAssignedTransportReady { get; }

        void WriteSyncedSkipped(int offlineCount);

        Task NotifyMessagesReleasedAsync(
            int offlineCount,
            bool firstRelease,
            bool transportReady);

        void SchedulePostReplayMaintenance(int offlineCount);

        bool IsHistoryCatchUpActive { get; }

        void LogHistoryFreshnessAfterOfflineDrain(int offlineCount);
    }
}
