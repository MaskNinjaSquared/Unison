// =============================================================================
// ChatCatalogDiskPersistence
//
// Owns IChatCatalogFullPersistHost + ILoadPersistedChatCatalogHost (full /
// slice / suspend-tail / startup hydrate). Debounce timer stays on the client.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ChatCatalogDiskPersistence :
        IChatCatalogFullPersistHost,
        ILoadPersistedChatCatalogHost
    {
        private readonly ChatStateStore _chats;
        private readonly JidAliasTable _aliases;
        private readonly IMessageStore _messages;
        private readonly IHistoryChatPreviewStore _previews;
        private readonly IDispatcher _ui;
        private readonly Func<IHistoryService> _history;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly SemaphoreSlim _persistRunLock = new SemaphoreSlim(1, 1);

        public ChatCatalogDiskPersistence(
            ChatStateStore chats,
            JidAliasTable aliases,
            IMessageStore messages,
            IHistoryChatPreviewStore previews,
            IDispatcher ui,
            Func<IHistoryService> history,
            IRuntimeDiagnostics diagnostics = null)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _previews = previews ?? throw new ArgumentNullException(nameof(previews));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _diagnostics = diagnostics;
        }

        public Task<bool> PersistFullAsync() =>
            ChatCatalogFullPersistDispatch.RunFullAsync(this);

        public Task PersistSliceAsync(IList<ChatItem> chats) =>
            ChatCatalogFullPersistDispatch.RunSliceAsync(this, chats);

        public Task PersistSuspendTailAsync() =>
            ChatCatalogFullPersistDispatch.RunSuspendTailAsync(this);

        public Task LoadPersistedChatsAsync() =>
            LoadPersistedChatCatalogDispatch.RunAsync(this);

        Task IChatCatalogFullPersistHost.EnterPersistLockAsync() =>
            _persistRunLock.WaitAsync();

        void IChatCatalogFullPersistHost.ExitPersistLock() =>
            _persistRunLock.Release();

        async Task IChatCatalogFullPersistHost.PersistFullCatalogAndNameMapsAsync()
        {
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> contactSnapshot = null;
            Dictionary<string, string> phoneContactSnapshot = null;
            Dictionary<string, string> aliasSnapshot = null;
            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(_chats.Chats);
                contactSnapshot = _chats.PushNames.Snapshot();
                phoneContactSnapshot = _chats.AddressBookNames.Snapshot();
                aliasSnapshot = _aliases.Snapshot();
            }).ConfigureAwait(false);

            IEnumerable<string> chatJids = catalog?.ChatJids ?? (IEnumerable<string>)Array.Empty<string>();
            await ChatCatalogPreviewPersist.UpsertAsync(_previews, catalog).ConfigureAwait(false);
            await _messages.SaveContactNamesAsync(
                contactSnapshot ?? new Dictionary<string, string>(),
                chatJids).ConfigureAwait(false);
            await _messages.SavePhoneContactNamesAsync(
                phoneContactSnapshot ?? new Dictionary<string, string>(),
                chatJids).ConfigureAwait(false);
            await _messages.SaveJidAliasesAsync(
                aliasSnapshot ?? new Dictionary<string, string>(),
                chatJids).ConfigureAwait(false);

            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Persisted " + (catalog?.Rows.Count ?? 0) +
                " chat rows and contact metadata");
        }

        void IChatCatalogFullPersistHost.LogFullPersistFailed(Exception error) =>
            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Failed to persist data: " +
                (error != null ? error.Message : string.Empty));

        async Task IChatCatalogFullPersistHost.PersistCatalogSliceAsync(IList<ChatItem> chats)
        {
            var sw = Stopwatch.StartNew();
            await ChatCatalogPreviewPersist.UpsertAsync(
                    _previews,
                    ChatCatalogSnapshot.Take(chats))
                .ConfigureAwait(false);
            try
            {
                _diagnostics?.Write(
                    "chat-open",
                    "persist-rows",
                    "rows=" + (chats?.Count ?? 0) + "; elapsedMs=" + sw.ElapsedMilliseconds);
            }
            catch
            {
            }
        }

        void IChatCatalogFullPersistHost.LogCatalogSliceSucceeded(int rowCount) =>
            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Persisted chat-preview slice rows=" + rowCount);

        void IChatCatalogFullPersistHost.LogCatalogSliceFailed(Exception error) =>
            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Chat-preview slice persist failed: " +
                (error != null ? error.Message : string.Empty));

        async Task IChatCatalogFullPersistHost.PersistCriticalSuspendStateAsync()
        {
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> aliasSnapshot = null;
            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(_chats.Chats);
                aliasSnapshot = _aliases.Snapshot();
            }).ConfigureAwait(false);

            await ChatCatalogPreviewPersist.UpsertAsync(_previews, catalog).ConfigureAwait(false);
            await _messages.SaveJidAliasesAsync(
                aliasSnapshot ?? new Dictionary<string, string>(),
                catalog?.ChatJids ?? (IEnumerable<string>)Array.Empty<string>())
                .ConfigureAwait(false);

            try
            {
                _diagnostics?.Write(
                    "lifecycle",
                    "fast-suspend-persisted",
                    "chatRows=" + (catalog?.Rows.Count ?? 0));
            }
            catch
            {
            }
        }

        void IChatCatalogFullPersistHost.LogSuspendPersistFailed(Exception error)
        {
            try
            {
                _diagnostics?.RecordException(
                    "lifecycle",
                    "suspend-persist-tail-failed",
                    error);
            }
            catch
            {
            }
        }

        IDisposable ILoadPersistedChatCatalogHost.BeginPersistedChatsLoad() =>
            _chats.BeginPersistedChatsLoad();

        Task<IReadOnlyList<HistoryChatPreview>> ILoadPersistedChatCatalogHost.ReadAllPreviewsAsync() =>
            _previews.GetAllAsync();

        Task ILoadPersistedChatCatalogHost.ApplyPreviewsToChatListAsync(
            IReadOnlyList<HistoryChatPreview> stored)
        {
            return _ui.RunAsync(() =>
            {
                var existing = new HashSet<string>(
                    _chats.Chats
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                        .Select(c => JidHelper.Normalize(c.JID)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (HistoryChatPreview preview in stored)
                {
                    ChatItem chat = HistoryChatPreviewApplier.ToChatItemForCatalog(preview);
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    string normJid = JidHelper.Normalize(chat.JID);
                    chat.JID = normJid;
                    ChatPreviewNormalizer.ApplyToChatItem(chat);
                    chat.ApplyKind(normJid, _aliases.IsSelfLinked(normJid));
                    if (existing.Add(normJid))
                    {
                        _chats.Chats.Add(chat);
                    }
                }

                foreach (ChatItem chat in _chats.Chats)
                {
                    if (chat != null)
                    {
                        chat.ApplyKind(chat.JID, _aliases.IsSelfLinked(chat.JID));
                    }
                }

                ChatDisplayOrder.SortInPlace(_chats.Chats);
            });
        }

        void ILoadPersistedChatCatalogHost.LogLoaded(int previewCount) =>
            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Fast startup loaded " + previewCount +
                " chat preview rows");

        void ILoadPersistedChatCatalogHost.NotifyHistorySyncAfterLoad()
        {
            try
            {
                _history()?.NotifyHistorySyncReceived(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ChatCatalogDiskPersistence] NotifyHistorySyncAfterLoad failed: " +
                    ex.Message);
            }
        }

        void ILoadPersistedChatCatalogHost.LogLoadFailed(Exception error) =>
            Debug.WriteLine(
                "[ChatCatalogDiskPersistence] Failed to load persisted chat metadata: " +
                (error != null ? error.Message : string.Empty));
    }
}
