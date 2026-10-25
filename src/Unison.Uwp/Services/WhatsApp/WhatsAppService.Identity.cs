using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {
        private bool TryGetSelfIdentityAliasPair(out string id, out string lid)
        {
            id = null;
            lid = null;
            if (_authState?.Me == null)
            {
                return false;
            }

            return SelfIdentityAliasPair.TryCreate(
                _authState.Me.Id,
                _authState.Me.Lid,
                NormalizeJid,
                out id,
                out lid);
        }

        private void FileSelfIdentityAliases(string source)
        {
            string id;
            string lid;
            if (!TryGetSelfIdentityAliasPair(out id, out lid))
            {
                return;
            }

            JidAlias[id] = lid;
            JidAlias[lid] = id;
            RegisterSocketAlias(id, lid, source);
            Debug.WriteLine("[WhatsAppService] Self identity alias: " + id + " <-> " + lid + " (" + source + ")");
        }

        private void RegisterSocketAlias(string jidA, string jidB, string source)
        {
            _socket?.RegisterJidAlias(jidA, jidB, source);
        }

        internal void RegisterSocketAliases(string source)
        {
            var aliases = JidAlias.Snapshot();
            _socket?.RegisterJidAliases(aliases, source);
        }

        private Task PersistChatIdentityStateAsync(string reason) =>
            PersistChatIdentityStateDispatch.RunAsync(this, reason);

        // --- IPersistChatIdentityStateHost -----------------------------------

        async Task<ChatIdentityCapture> IPersistChatIdentityStateHost.CaptureChatIdentitySnapshotsAsync()
        {
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> contactSnapshot = null;
            Dictionary<string, string> phoneSnapshot = null;
            Dictionary<string, string> aliasSnapshot = null;

            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(Chats);
                contactSnapshot = ContactNames.Snapshot();
                phoneSnapshot = PhoneContactNamesByJid.Snapshot();
                aliasSnapshot = JidAlias.Snapshot();
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
            PersistChatCatalogAsync(catalog);

        Task IPersistChatIdentityStateHost.SaveContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids) =>
            _messageStore.SaveContactNamesAsync(
                names as Dictionary<string, string> ?? new Dictionary<string, string>(names),
                chatJids);

        Task IPersistChatIdentityStateHost.SavePhoneContactNamesAsync(
            IReadOnlyDictionary<string, string> names,
            IReadOnlyList<string> chatJids) =>
            _messageStore.SavePhoneContactNamesAsync(
                names as Dictionary<string, string> ?? new Dictionary<string, string>(names),
                chatJids);

        Task IPersistChatIdentityStateHost.SaveJidAliasesAsync(
            IReadOnlyDictionary<string, string> aliases,
            IReadOnlyList<string> chatJids) =>
            _messageStore.SaveJidAliasesAsync(
                aliases as Dictionary<string, string> ?? new Dictionary<string, string>(aliases),
                chatJids);

        void IPersistChatIdentityStateHost.LogPersistChatIdentity(string message) =>
            Debug.WriteLine("[WhatsAppService] " + message);

        public string ResolveDisplayName(string jid, string context = null) =>
            _displayNames.Resolve(jid, context);

        public string GetCanonicalJid(string jid) => JidAlias.GetCanonicalJid(jid);
    }
}
