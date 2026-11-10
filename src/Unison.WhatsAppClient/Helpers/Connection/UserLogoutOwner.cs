// =============================================================================
// UserLogoutOwner
//
// Owns IUserLogoutHost for ConnectionFacade.LogoutAsync.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class UserLogoutOwner : IUserLogoutHost
    {
        private readonly Func<bool> _tryBegin;
        private readonly Action _end;
        private readonly Action<string, string> _journal;
        private readonly Func<string, Task> _notifyAsync;
        private readonly Func<Task> _clearSessionAsync;
        private readonly Func<Task> _clearContactsAsync;
        private readonly Func<Task> _autoRestoreAsync;

        public UserLogoutOwner(
            Func<bool> tryBegin,
            Action end,
            Action<string, string> journal,
            Func<string, Task> notifyAsync,
            Func<Task> clearSessionAsync,
            Func<Task> clearContactsAsync,
            Func<Task> autoRestoreAsync)
        {
            _tryBegin = tryBegin ?? throw new ArgumentNullException(nameof(tryBegin));
            _end = end ?? throw new ArgumentNullException(nameof(end));
            _journal = journal ?? throw new ArgumentNullException(nameof(journal));
            _notifyAsync = notifyAsync ?? throw new ArgumentNullException(nameof(notifyAsync));
            _clearSessionAsync = clearSessionAsync
                ?? throw new ArgumentNullException(nameof(clearSessionAsync));
            _clearContactsAsync = clearContactsAsync
                ?? throw new ArgumentNullException(nameof(clearContactsAsync));
            _autoRestoreAsync = autoRestoreAsync
                ?? throw new ArgumentNullException(nameof(autoRestoreAsync));
        }

        public Task RunAsync(string reason = null, TimeSpan? notifyTimeout = null) =>
            UserLogoutDispatch.RunAsync(this, reason, notifyTimeout);

        bool IUserLogoutHost.TryBeginLogout() => _tryBegin();

        void IUserLogoutHost.EndLogout() => _end();

        void IUserLogoutHost.JournalLogout(string eventName, string detail) =>
            _journal(eventName, detail);

        Task IUserLogoutHost.NotifyServerLogoutAsync(string reason) =>
            _notifyAsync(reason);

        Task IUserLogoutHost.ClearSessionAsync() => _clearSessionAsync();

        Task IUserLogoutHost.ClearPublishedContactsAsync() => _clearContactsAsync();

        Task IUserLogoutHost.AutoRestoreDefaultAsync() => _autoRestoreAsync();
    }
}
