// =============================================================================
// RaiseSessionClearedOwner
//
// Owns IRaiseSessionClearedHost. History notify / UI event stay as ctor ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class RaiseSessionClearedOwner : IRaiseSessionClearedHost
    {
        private readonly Action<string> _resetFirstSyncListHold;
        private readonly Action _notifyHistoryCleared;
        private readonly Action<string> _log;
        private readonly Func<bool, Task> _raiseOnUiAsync;

        public RaiseSessionClearedOwner(
            Action<string> resetFirstSyncListHold,
            Action notifyHistoryCleared,
            Action<string> log,
            Func<bool, Task> raiseOnUiAsync)
        {
            _resetFirstSyncListHold = resetFirstSyncListHold
                ?? throw new ArgumentNullException(nameof(resetFirstSyncListHold));
            _notifyHistoryCleared = notifyHistoryCleared
                ?? throw new ArgumentNullException(nameof(notifyHistoryCleared));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _raiseOnUiAsync = raiseOnUiAsync
                ?? throw new ArgumentNullException(nameof(raiseOnUiAsync));
        }

        public Task RunAsync(bool startPairing) =>
            RaiseSessionClearedDispatch.RunAsync(this, startPairing);

        void IRaiseSessionClearedHost.ResetFirstSyncListHoldFlags(string reason) =>
            _resetFirstSyncListHold(reason);

        void IRaiseSessionClearedHost.NotifyHistorySessionCleared() =>
            _notifyHistoryCleared();

        void IRaiseSessionClearedHost.LogSessionCleared(string message) =>
            _log(message);

        Task IRaiseSessionClearedHost.RaiseOnSessionClearedOnUiAsync(bool startPairing) =>
            _raiseOnUiAsync(startPairing);
    }
}
