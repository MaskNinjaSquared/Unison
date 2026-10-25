// =============================================================================
// ClearSessionOwner
//
// Owns IClearSessionHost. Store / socket / UI wipe stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ClearSessionOwner : IClearSessionHost
    {
        private readonly Action<string> _log;
        private readonly Action _captureKeyStore;
        private readonly Action _suppressReconnectToast;
        private readonly Action _clearReconnectToast;
        private readonly Func<Task> _clearAuthEarlyAsync;
        private readonly Action _nullAuthState;
        private readonly Action _armFatalFlags;
        private readonly Action<string> _stopHealthMonitor;
        private readonly Action _disconnectSocket;
        private readonly Action _resetSessionSignal;
        private readonly Func<bool, Task> _raiseSessionClearedAsync;
        private readonly Func<Task> _yieldAsync;
        private readonly Func<Task> _clearFileKeyStoreAsync;
        private readonly Func<Task> _clearAuthIdempotentAsync;
        private readonly Action _dropSharedFlags;
        private readonly Func<Task> _clearNoiseAsync;
        private readonly Action _allowPairing;
        private readonly Func<Task> _wipeMessagesAsync;
        private readonly Func<Task> _clearRosterAsync;
        private readonly Func<Task> _clearMemoryAsync;
        private readonly Action _clearNotifications;

        public ClearSessionOwner(
            Action<string> log,
            Action captureKeyStore,
            Action suppressReconnectToast,
            Action clearReconnectToast,
            Func<Task> clearAuthEarlyAsync,
            Action nullAuthState,
            Action armFatalFlags,
            Action<string> stopHealthMonitor,
            Action disconnectSocket,
            Action resetSessionSignal,
            Func<bool, Task> raiseSessionClearedAsync,
            Func<Task> yieldAsync,
            Func<Task> clearFileKeyStoreAsync,
            Func<Task> clearAuthIdempotentAsync,
            Action dropSharedFlags,
            Func<Task> clearNoiseAsync,
            Action allowPairing,
            Func<Task> wipeMessagesAsync,
            Func<Task> clearRosterAsync,
            Func<Task> clearMemoryAsync,
            Action clearNotifications)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _captureKeyStore = captureKeyStore
                ?? throw new ArgumentNullException(nameof(captureKeyStore));
            _suppressReconnectToast = suppressReconnectToast
                ?? throw new ArgumentNullException(nameof(suppressReconnectToast));
            _clearReconnectToast = clearReconnectToast
                ?? throw new ArgumentNullException(nameof(clearReconnectToast));
            _clearAuthEarlyAsync = clearAuthEarlyAsync
                ?? throw new ArgumentNullException(nameof(clearAuthEarlyAsync));
            _nullAuthState = nullAuthState
                ?? throw new ArgumentNullException(nameof(nullAuthState));
            _armFatalFlags = armFatalFlags
                ?? throw new ArgumentNullException(nameof(armFatalFlags));
            _stopHealthMonitor = stopHealthMonitor
                ?? throw new ArgumentNullException(nameof(stopHealthMonitor));
            _disconnectSocket = disconnectSocket
                ?? throw new ArgumentNullException(nameof(disconnectSocket));
            _resetSessionSignal = resetSessionSignal
                ?? throw new ArgumentNullException(nameof(resetSessionSignal));
            _raiseSessionClearedAsync = raiseSessionClearedAsync
                ?? throw new ArgumentNullException(nameof(raiseSessionClearedAsync));
            _yieldAsync = yieldAsync ?? throw new ArgumentNullException(nameof(yieldAsync));
            _clearFileKeyStoreAsync = clearFileKeyStoreAsync
                ?? throw new ArgumentNullException(nameof(clearFileKeyStoreAsync));
            _clearAuthIdempotentAsync = clearAuthIdempotentAsync
                ?? throw new ArgumentNullException(nameof(clearAuthIdempotentAsync));
            _dropSharedFlags = dropSharedFlags
                ?? throw new ArgumentNullException(nameof(dropSharedFlags));
            _clearNoiseAsync = clearNoiseAsync
                ?? throw new ArgumentNullException(nameof(clearNoiseAsync));
            _allowPairing = allowPairing
                ?? throw new ArgumentNullException(nameof(allowPairing));
            _wipeMessagesAsync = wipeMessagesAsync
                ?? throw new ArgumentNullException(nameof(wipeMessagesAsync));
            _clearRosterAsync = clearRosterAsync
                ?? throw new ArgumentNullException(nameof(clearRosterAsync));
            _clearMemoryAsync = clearMemoryAsync
                ?? throw new ArgumentNullException(nameof(clearMemoryAsync));
            _clearNotifications = clearNotifications
                ?? throw new ArgumentNullException(nameof(clearNotifications));
        }

        public Task RunAsync() => ClearSessionDispatch.RunAsync(this);

        void IClearSessionHost.LogClearSession(string message) => _log(message);

        void IClearSessionHost.CaptureKeyStoreBeforeDisconnect() => _captureKeyStore();

        void IClearSessionHost.SuppressReconnectToastForClear() => _suppressReconnectToast();

        void IClearSessionHost.ClearReconnectRequiredToast() => _clearReconnectToast();

        Task IClearSessionHost.ClearAuthStoreEarlyAsync() => _clearAuthEarlyAsync();

        void IClearSessionHost.NullAuthState() => _nullAuthState();

        void IClearSessionHost.ArmFatalDisconnectFlagsForClear() => _armFatalFlags();

        void IClearSessionHost.StopConnectionHealthMonitor(string reason) =>
            _stopHealthMonitor(reason);

        void IClearSessionHost.DisconnectAndDisposeSocket() => _disconnectSocket();

        void IClearSessionHost.ResetSessionEstablishedSignal() => _resetSessionSignal();

        Task IClearSessionHost.RaiseSessionClearedAsync(bool startPairing) =>
            _raiseSessionClearedAsync(startPairing);

        Task IClearSessionHost.YieldAsync() => _yieldAsync();

        Task IClearSessionHost.ClearFileKeyStoreAsync() => _clearFileKeyStoreAsync();

        Task IClearSessionHost.ClearAuthStoreIdempotentAsync() => _clearAuthIdempotentAsync();

        void IClearSessionHost.DropSharedKeyStoreAndResumeFlags() => _dropSharedFlags();

        Task IClearSessionHost.ClearNoiseSessionAsync() => _clearNoiseAsync();

        void IClearSessionHost.AllowPairingAfterAuthWipe() => _allowPairing();

        Task IClearSessionHost.WipeMessageStoreAsync() => _wipeMessagesAsync();

        Task IClearSessionHost.ClearGroupRosterAsync() => _clearRosterAsync();

        Task IClearSessionHost.ClearInMemorySessionStateAsync() => _clearMemoryAsync();

        void IClearSessionHost.ClearNotifications() => _clearNotifications();
    }
}
