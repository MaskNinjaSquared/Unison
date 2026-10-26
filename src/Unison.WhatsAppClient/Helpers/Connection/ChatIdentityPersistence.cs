// =============================================================================
// ChatIdentityPersistence
//
// Owns IChatIdentityPersistence + IPersistChatIdentityStateHost. Immediate
// catalogue + name + alias flush after list fold / dedupe — same ownership
// shape as JidAliasPersistence (dispatch host left WhatsAppService).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ChatIdentityPersistence :
        IChatIdentityPersistence,
        IPersistChatIdentityStateHost
    {
        private readonly ChatStateStore _chats;
        private readonly JidAliasTable _aliases;
        private readonly IMessageStore _messages;
        private readonly IHistoryChatPreviewStore _previews;
        private readonly IDispatcher _ui;

        public ChatIdentityPersistence(
            ChatStateStore chats,
            JidAliasTable aliases,
            IMessageStore messages,
            IHistoryChatPreviewStore previews,
            IDispatcher ui)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _previews = previews ?? throw new ArgumentNullException(nameof(previews));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        }

        /// <inheritdoc />
        public void PersistNow(string reason) =>
            _ = PersistChatIdentityStateDispatch.RunAsync(this, reason ?? "facade");

        // --- IPersistChatIdentityStateHost -----------------------------------

        async Task<ChatIdentityCapture> IPersistChatIdentityStateHost.CaptureChatIdentitySnapshotsAsync()
        {
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> contactSnapshot = null;
            Dictionary<string, string> phoneSnapshot = null;
            Dictionary<string, string> aliasSnapshot = null;

            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(_chats.Chats);
                contactSnapshot = _chats.PushNames.Snapshot();
                phoneSnapshot = _chats.AddressBookNames.Snapshot();
                aliasSnapshot = _aliases.Snapshot();
            }).ConfigureAwait(false);

            return new ChatIdentityCapture
            {
                Catalog = catalog,
                ContactNames = contactSnapshot,
                PhoneNames = phoneSnapshot,
                Aliases = aliasSnapshot
            };
        }

        Task IPersistChatIdentityStateHost.PersistChatCatalogAsync(ChatCatalogSnapshot catalog) =>
            ChatCatalogPreviewPersist.UpsertAsync(_previews, catalog);

        Task IPersistChatIdentityStateHost.SaveContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids) =>
            _messages.SaveContactNamesAsync(ToDictionary(names), chatJids);

        Task IPersistChatIdentityStateHost.SavePhoneContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids) =>
            _messages.SavePhoneContactNamesAsync(ToDictionary(names), chatJids);

        Task IPersistChatIdentityStateHost.SaveJidAliasesAsync(
            IReadOnlyDictionary<string, string> aliases,
            IReadOnlyList<string> chatJids) =>
            _messages.SaveJidAliasesAsync(ToDictionary(aliases), chatJids);

        void IPersistChatIdentityStateHost.LogPersistChatIdentity(string message) =>
            Debug.WriteLine("[ChatIdentityPersistence] " + message);

        private static Dictionary<string, string> ToDictionary(
            IReadOnlyDictionary<string, string> source)
        {
            Dictionary<string, string> map = source as Dictionary<string, string>;
            if (map != null)
            {
                return map;
            }

            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (KeyValuePair<string, string> pair in source)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }
    }
}
