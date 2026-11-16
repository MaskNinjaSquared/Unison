// =============================================================================
// ResyncConversationsOwner
//
// Owns IResyncConversationsHost. Store wipe / cache clear / catalogue persist /
// notifications / history wait stay as ports.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ResyncConversationsOwner : IResyncConversationsHost
    {
        private readonly Action<string> _log;
        private readonly Func<Task> _wipeChatsAndMessagesAsync;
        private readonly Func<Task> _clearConversationCachesAsync;
        private readonly Func<Task> _persistEmptyChatCatalogAsync;
        private readonly Action _clearAllNotifications;
        private readonly ILocalSettings _settings;
        private readonly Func<Task> _waitConversationResyncAsync;

        public ResyncConversationsOwner(
            Action<string> log,
            Func<Task> wipeChatsAndMessagesAsync,
            Func<Task> clearConversationCachesAsync,
            Func<Task> persistEmptyChatCatalogAsync,
            Action clearAllNotifications,
            ILocalSettings settings,
            Func<Task> waitConversationResyncAsync)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _wipeChatsAndMessagesAsync = wipeChatsAndMessagesAsync
                ?? throw new ArgumentNullException(nameof(wipeChatsAndMessagesAsync));
            _clearConversationCachesAsync = clearConversationCachesAsync
                ?? throw new ArgumentNullException(nameof(clearConversationCachesAsync));
            _persistEmptyChatCatalogAsync = persistEmptyChatCatalogAsync
                ?? throw new ArgumentNullException(nameof(persistEmptyChatCatalogAsync));
            _clearAllNotifications = clearAllNotifications
                ?? throw new ArgumentNullException(nameof(clearAllNotifications));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _waitConversationResyncAsync = waitConversationResyncAsync
                ?? throw new ArgumentNullException(nameof(waitConversationResyncAsync));
        }

        public Task RunAsync(IProgress<ConversationResyncPhase> progress = null) =>
            ResyncConversationsDispatch.RunAsync(this, progress);

        void IResyncConversationsHost.LogResync(string message) => _log(message);

        void IResyncConversationsHost.ReportResyncPhase(
            IProgress<ConversationResyncPhase> progress,
            ConversationResyncPhase phase) =>
            progress?.Report(phase);

        Task IResyncConversationsHost.WipeChatsAndMessagesAsync() =>
            _wipeChatsAndMessagesAsync();

        Task IResyncConversationsHost.ClearConversationCachesAsync() =>
            _clearConversationCachesAsync();

        async Task IResyncConversationsHost.PersistEmptyChatCatalogAsync()
        {
            try
            {
                await _persistEmptyChatCatalogAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log("Warning: failed to persist empty chats after resync wipe: " + ex.Message);
            }
        }

        void IResyncConversationsHost.ClearAllNotificationsBestEffort()
        {
            try
            {
                _clearAllNotifications();
            }
            catch (Exception ex)
            {
                _log("Warning: failed to clear notifications after resync: " + ex.Message);
            }
        }

        void IResyncConversationsHost.SetForceHistoryRepairPendingBestEffort()
        {
            try
            {
                _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);
            }
            catch (Exception ex)
            {
                _log("Warning: failed to set force-history flag after wipe: " + ex.Message);
            }
        }

        Task IResyncConversationsHost.WaitConversationResyncAsync() =>
            _waitConversationResyncAsync();
    }
}
