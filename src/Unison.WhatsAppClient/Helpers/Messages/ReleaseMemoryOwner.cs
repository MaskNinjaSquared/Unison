// =============================================================================
// ReleaseMemoryOwner
//
// Owns IReleaseMemoryHost. Message-store clear / UI hop / inactive-cache
// remove stay as ctor ports.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ReleaseMemoryOwner : IReleaseMemoryHost
    {
        private readonly Action _clearMessageStoreMemoryCache;
        private readonly Func<Action, Task> _runOnUiAsync;
        private readonly Func<string> _activeChatJid;
        private readonly Func<IReadOnlyList<string>> _messageCacheChatJids;
        private readonly Func<string, string> _getCanonicalJid;
        private readonly Action<string> _removeMessagesByChatKey;
        private readonly Action _logMemoryReleased;
        private readonly Action<Exception> _logMemoryReleaseFailed;

        public ReleaseMemoryOwner(
            Action clearMessageStoreMemoryCache,
            Func<Action, Task> runOnUiAsync,
            Func<string> activeChatJid,
            Func<IReadOnlyList<string>> messageCacheChatJids,
            Func<string, string> getCanonicalJid,
            Action<string> removeMessagesByChatKey,
            Action logMemoryReleased,
            Action<Exception> logMemoryReleaseFailed)
        {
            _clearMessageStoreMemoryCache = clearMessageStoreMemoryCache
                ?? throw new ArgumentNullException(nameof(clearMessageStoreMemoryCache));
            _runOnUiAsync = runOnUiAsync
                ?? throw new ArgumentNullException(nameof(runOnUiAsync));
            _activeChatJid = activeChatJid
                ?? throw new ArgumentNullException(nameof(activeChatJid));
            _messageCacheChatJids = messageCacheChatJids
                ?? throw new ArgumentNullException(nameof(messageCacheChatJids));
            _getCanonicalJid = getCanonicalJid
                ?? throw new ArgumentNullException(nameof(getCanonicalJid));
            _removeMessagesByChatKey = removeMessagesByChatKey
                ?? throw new ArgumentNullException(nameof(removeMessagesByChatKey));
            _logMemoryReleased = logMemoryReleased
                ?? throw new ArgumentNullException(nameof(logMemoryReleased));
            _logMemoryReleaseFailed = logMemoryReleaseFailed
                ?? throw new ArgumentNullException(nameof(logMemoryReleaseFailed));
        }

        public Task RunAsync() => ReleaseMemoryDispatch.RunAsync(this);

        void IReleaseMemoryHost.ClearMessageStoreMemoryCache() =>
            _clearMessageStoreMemoryCache();

        Task IReleaseMemoryHost.RunOnUiAsync(Action action) =>
            _runOnUiAsync(action);

        string IReleaseMemoryHost.ActiveChatJid =>
            _activeChatJid();

        IReadOnlyList<string> IReleaseMemoryHost.MessageCacheChatJids() =>
            _messageCacheChatJids();

        string IReleaseMemoryHost.GetCanonicalJid(string jid) =>
            _getCanonicalJid(jid);

        void IReleaseMemoryHost.RemoveMessagesByChatKey(string key) =>
            _removeMessagesByChatKey(key);

        void IReleaseMemoryHost.LogMemoryReleased() =>
            _logMemoryReleased();

        void IReleaseMemoryHost.LogMemoryReleaseFailed(Exception error) =>
            _logMemoryReleaseFailed(error);
    }
}
