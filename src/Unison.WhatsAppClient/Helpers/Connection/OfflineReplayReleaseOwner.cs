// =============================================================================
// OfflineReplayReleaseOwner
//
// Owns IOfflineReplayReleaseHost. Flush / façade settle / post-replay schedule
// stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class OfflineReplayReleaseOwner : IOfflineReplayReleaseHost
    {
        private readonly Func<bool> _tryMarkFirstRelease;
        private readonly Action<int, bool> _writeReleaseDiagnostics;
        private readonly Action _clearReplayDrainPending;
        private readonly Func<string, Task> _flushOfflineReplay;
        private readonly Func<bool> _isAssignedTransportReady;
        private readonly Action<int> _writeSyncedSkipped;
        private readonly Func<int, bool, bool, Task> _notifyMessagesReleased;
        private readonly Action<int> _schedulePostReplayMaintenance;
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Action<int> _logHistoryFreshness;

        public OfflineReplayReleaseOwner(
            Func<bool> tryMarkFirstRelease,
            Action<int, bool> writeReleaseDiagnostics,
            Action clearReplayDrainPending,
            Func<string, Task> flushOfflineReplay,
            Func<bool> isAssignedTransportReady,
            Action<int> writeSyncedSkipped,
            Func<int, bool, bool, Task> notifyMessagesReleased,
            Action<int> schedulePostReplayMaintenance,
            Func<bool> isHistoryCatchUpActive,
            Action<int> logHistoryFreshness)
        {
            _tryMarkFirstRelease = tryMarkFirstRelease
                ?? throw new ArgumentNullException(nameof(tryMarkFirstRelease));
            _writeReleaseDiagnostics = writeReleaseDiagnostics
                ?? throw new ArgumentNullException(nameof(writeReleaseDiagnostics));
            _clearReplayDrainPending = clearReplayDrainPending
                ?? throw new ArgumentNullException(nameof(clearReplayDrainPending));
            _flushOfflineReplay = flushOfflineReplay
                ?? throw new ArgumentNullException(nameof(flushOfflineReplay));
            _isAssignedTransportReady = isAssignedTransportReady
                ?? throw new ArgumentNullException(nameof(isAssignedTransportReady));
            _writeSyncedSkipped = writeSyncedSkipped
                ?? throw new ArgumentNullException(nameof(writeSyncedSkipped));
            _notifyMessagesReleased = notifyMessagesReleased
                ?? throw new ArgumentNullException(nameof(notifyMessagesReleased));
            _schedulePostReplayMaintenance = schedulePostReplayMaintenance
                ?? throw new ArgumentNullException(nameof(schedulePostReplayMaintenance));
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _logHistoryFreshness = logHistoryFreshness
                ?? throw new ArgumentNullException(nameof(logHistoryFreshness));
        }

        public Task RunAsync(int offlineCount) =>
            OfflineReplayRelease.RunAsync(this, offlineCount);

        bool IOfflineReplayReleaseHost.TryMarkFirstRelease() =>
            _tryMarkFirstRelease();

        void IOfflineReplayReleaseHost.WriteReleaseDiagnostics(int offlineCount, bool firstRelease) =>
            _writeReleaseDiagnostics(offlineCount, firstRelease);

        void IOfflineReplayReleaseHost.ClearReplayDrainPending() =>
            _clearReplayDrainPending();

        Task IOfflineReplayReleaseHost.FlushOfflineReplayMessagesAsync(string reason) =>
            _flushOfflineReplay(reason);

        bool IOfflineReplayReleaseHost.IsAssignedTransportReady =>
            _isAssignedTransportReady();

        void IOfflineReplayReleaseHost.WriteSyncedSkipped(int offlineCount) =>
            _writeSyncedSkipped(offlineCount);

        Task IOfflineReplayReleaseHost.NotifyMessagesReleasedAsync(
            int offlineCount,
            bool firstRelease,
            bool transportReady) =>
            _notifyMessagesReleased(offlineCount, firstRelease, transportReady);

        void IOfflineReplayReleaseHost.SchedulePostReplayMaintenance(int offlineCount) =>
            _schedulePostReplayMaintenance(offlineCount);

        bool IOfflineReplayReleaseHost.IsHistoryCatchUpActive =>
            _isHistoryCatchUpActive();

        void IOfflineReplayReleaseHost.LogHistoryFreshnessAfterOfflineDrain(int offlineCount) =>
            _logHistoryFreshness(offlineCount);
    }
}
