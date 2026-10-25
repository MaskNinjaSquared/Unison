// =============================================================================
// SessionInitializedOwner
//
// Owns ISessionInitializedHost. Pairing flags / persist / post-replay stay as
// ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SessionInitializedOwner : ISessionInitializedHost
    {
        private readonly Func<bool> _fatalSessionEnded;
        private readonly Action _markSessionEstablished;
        private readonly Action _clearPairingRestartPending;
        private readonly Action _enablePreSessionCloseAsFatal;
        private readonly Action _resetPreSessionCloseStreak;
        private readonly Action _completeSessionEstablishedWait;
        private readonly Action _ensureSelfPhonePersisted;
        private readonly Func<Task> _persistAuthStateAsync;
        private readonly Func<bool> _shouldDeferReconnectReplayWork;
        private readonly Action _logDeferringReconnectWork;
        private readonly Action _schedulePostReplayMaintenance;
        private readonly Action<string> _startForceHistoryRepair;
        private readonly Action _raiseSessionInitialized;
        private readonly Action<string> _pairingTrace;
        private readonly Action _logSessionInitialized;

        public SessionInitializedOwner(
            Func<bool> fatalSessionEnded,
            Action markSessionEstablished,
            Action clearPairingRestartPending,
            Action enablePreSessionCloseAsFatal,
            Action resetPreSessionCloseStreak,
            Action completeSessionEstablishedWait,
            Action ensureSelfPhonePersisted,
            Func<Task> persistAuthStateAsync,
            Func<bool> shouldDeferReconnectReplayWork,
            Action logDeferringReconnectWork,
            Action schedulePostReplayMaintenance,
            Action<string> startForceHistoryRepair,
            Action raiseSessionInitialized,
            Action<string> pairingTrace,
            Action logSessionInitialized)
        {
            _fatalSessionEnded = fatalSessionEnded
                ?? throw new ArgumentNullException(nameof(fatalSessionEnded));
            _markSessionEstablished = markSessionEstablished
                ?? throw new ArgumentNullException(nameof(markSessionEstablished));
            _clearPairingRestartPending = clearPairingRestartPending
                ?? throw new ArgumentNullException(nameof(clearPairingRestartPending));
            _enablePreSessionCloseAsFatal = enablePreSessionCloseAsFatal
                ?? throw new ArgumentNullException(nameof(enablePreSessionCloseAsFatal));
            _resetPreSessionCloseStreak = resetPreSessionCloseStreak
                ?? throw new ArgumentNullException(nameof(resetPreSessionCloseStreak));
            _completeSessionEstablishedWait = completeSessionEstablishedWait
                ?? throw new ArgumentNullException(nameof(completeSessionEstablishedWait));
            _ensureSelfPhonePersisted = ensureSelfPhonePersisted
                ?? throw new ArgumentNullException(nameof(ensureSelfPhonePersisted));
            _persistAuthStateAsync = persistAuthStateAsync
                ?? throw new ArgumentNullException(nameof(persistAuthStateAsync));
            _shouldDeferReconnectReplayWork = shouldDeferReconnectReplayWork
                ?? throw new ArgumentNullException(nameof(shouldDeferReconnectReplayWork));
            _logDeferringReconnectWork = logDeferringReconnectWork
                ?? throw new ArgumentNullException(nameof(logDeferringReconnectWork));
            _schedulePostReplayMaintenance = schedulePostReplayMaintenance
                ?? throw new ArgumentNullException(nameof(schedulePostReplayMaintenance));
            _startForceHistoryRepair = startForceHistoryRepair
                ?? throw new ArgumentNullException(nameof(startForceHistoryRepair));
            _raiseSessionInitialized = raiseSessionInitialized
                ?? throw new ArgumentNullException(nameof(raiseSessionInitialized));
            _pairingTrace = pairingTrace
                ?? throw new ArgumentNullException(nameof(pairingTrace));
            _logSessionInitialized = logSessionInitialized
                ?? throw new ArgumentNullException(nameof(logSessionInitialized));
        }

        public Task RunAsync() => SessionInitializedDispatch.RunAsync(this);

        bool ISessionInitializedHost.FatalSessionEnded => _fatalSessionEnded();

        void ISessionInitializedHost.MarkSessionEstablished() =>
            _markSessionEstablished();

        void ISessionInitializedHost.ClearPairingRestartPending() =>
            _clearPairingRestartPending();

        void ISessionInitializedHost.EnablePreSessionCloseAsFatal() =>
            _enablePreSessionCloseAsFatal();

        void ISessionInitializedHost.ResetPreSessionCloseStreak() =>
            _resetPreSessionCloseStreak();

        void ISessionInitializedHost.CompleteSessionEstablishedWait() =>
            _completeSessionEstablishedWait();

        void ISessionInitializedHost.EnsureSelfPhonePersisted() =>
            _ensureSelfPhonePersisted();

        Task ISessionInitializedHost.PersistAuthStateForSessionAsync() =>
            _persistAuthStateAsync();

        bool ISessionInitializedHost.ShouldDeferReconnectReplayWork() =>
            _shouldDeferReconnectReplayWork();

        void ISessionInitializedHost.LogDeferringReconnectWork() =>
            _logDeferringReconnectWork();

        void ISessionInitializedHost.SchedulePostReplayMaintenance() =>
            _schedulePostReplayMaintenance();

        void ISessionInitializedHost.StartMessageStoreForceHistoryRepair(string reason) =>
            _startForceHistoryRepair(reason);

        void ISessionInitializedHost.RaiseSessionInitialized() =>
            _raiseSessionInitialized();

        void ISessionInitializedHost.PairingTrace(string line) =>
            _pairingTrace(line);

        void ISessionInitializedHost.LogSessionInitialized() =>
            _logSessionInitialized();
    }
}
