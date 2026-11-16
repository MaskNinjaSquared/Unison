// =============================================================================
// UserResyncHistoryWaitOwner
//
// Owns the manual-resync history waiter (TCS) and both hosts that touch it:
// IConversationResyncWaitHost (arm / wait) and ICompleteUserResyncHistoryWaitHost
// (history landed). FULL_HISTORY send stays a port.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class UserResyncHistoryWaitOwner :
        IConversationResyncWaitHost,
        ICompleteUserResyncHistoryWaitHost
    {
        private readonly Action<string> _log;
        private readonly Action<string> _raiseStatus;
        private readonly Func<string, Task<bool>> _ensureFullHistoryRequestedAsync;
        private readonly ILocalSettings _settings;
        private TaskCompletionSource<bool> _waiter;

        public UserResyncHistoryWaitOwner(
            Action<string> log,
            Action<string> raiseStatus,
            Func<string, Task<bool>> ensureFullHistoryRequestedAsync,
            ILocalSettings settings)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _raiseStatus = raiseStatus ?? throw new ArgumentNullException(nameof(raiseStatus));
            _ensureFullHistoryRequestedAsync = ensureFullHistoryRequestedAsync
                ?? throw new ArgumentNullException(nameof(ensureFullHistoryRequestedAsync));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool IsWaiting =>
            Interlocked.CompareExchange(ref _waiter, null, null) != null;

        public Task WaitAsync() => ConversationResyncWaitDispatch.RunAsync(this);

        public void Complete(string reason) =>
            CompleteUserResyncHistoryWaitDispatch.Run(this, reason);

        /// <summary>Fails a pending waiter (FULL_HISTORY rejected). False when none was armed.</summary>
        public bool TryFail()
        {
            var failed = Interlocked.Exchange(ref _waiter, null);
            if (failed == null)
            {
                return false;
            }

            failed.TrySetResult(false);
            return true;
        }

        void IConversationResyncWaitHost.LogConversationResync(string message) => _log(message);

        void IConversationResyncWaitHost.RaiseConversationResyncStatus(string status) =>
            _raiseStatus(status);

        void IConversationResyncWaitHost.ArmUserResyncHistoryWait(
            TaskCompletionSource<bool> historyDone) =>
            Interlocked.Exchange(ref _waiter, historyDone)?.TrySetCanceled();

        Task<bool> IConversationResyncWaitHost.EnsureFullHistoryRequestedForUserResyncAsync(
            string reason) =>
            _ensureFullHistoryRequestedAsync(reason);

        void IConversationResyncWaitHost.CompleteUserResyncHistoryWaitFailed(
            TaskCompletionSource<bool> historyDone)
        {
            historyDone.TrySetResult(false);
            Interlocked.CompareExchange(ref _waiter, null, historyDone);
        }

        TimeSpan IConversationResyncWaitHost.HistoryWaitTimeout =>
            UserResyncFullHistoryGates.HistoryWaitTimeout;

        void IConversationResyncWaitHost.ClearUserResyncHistoryWait(
            TaskCompletionSource<bool> historyDone) =>
            Interlocked.CompareExchange(ref _waiter, null, historyDone);

        bool ICompleteUserResyncHistoryWaitHost.TryClaimUserResyncHistoryWaiter(out object waiter)
        {
            waiter = Interlocked.Exchange(ref _waiter, null);
            return waiter != null;
        }

        void ICompleteUserResyncHistoryWaitHost.ClearForceHistoryRepairPendingBestEffort()
        {
            try
            {
                _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, false);
            }
            catch
            {
                // Best-effort; history already arrived.
            }
        }

        void ICompleteUserResyncHistoryWaitHost.LogUserResyncHistoryWaitCompleted(string reason) =>
            _log("User resync history wait completed (" + reason + ").");

        void ICompleteUserResyncHistoryWaitHost.CompleteUserResyncHistoryWaiter(object waiter) =>
            (waiter as TaskCompletionSource<bool>)?.TrySetResult(true);
    }
}
