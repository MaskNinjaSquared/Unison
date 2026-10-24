using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
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

        // Changed pairs queued while JidAlias.BeginBulkImport is open so N×
        // RegisterJidAlias(1) (and the Bridge Store log) become one RegisterJidAliases.
        private readonly object _pendingSocketAliasGate = new object();
        private Dictionary<string, string> _pendingSocketAliases;

        private void QueuePendingSocketAlias(string jidA, string jidB)
        {
            if (string.IsNullOrWhiteSpace(jidA) || string.IsNullOrWhiteSpace(jidB))
            {
                return;
            }

            lock (_pendingSocketAliasGate)
            {
                if (_pendingSocketAliases == null)
                {
                    _pendingSocketAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                _pendingSocketAliases[jidA] = jidB;
                _pendingSocketAliases[jidB] = jidA;
            }
        }

        /// <summary>
        /// Pushes queued dual-write pairs to the socket in one batch. No-op while a
        /// bulk-import scope is still open (nested flushes defer to the outer end).
        /// </summary>
        private void FlushPendingSocketAliases(string source)
        {
            if (JidAlias != null && JidAlias.IsBulkImporting)
            {
                return;
            }

            Dictionary<string, string> pending;
            lock (_pendingSocketAliasGate)
            {
                pending = _pendingSocketAliases;
                _pendingSocketAliases = null;
            }

            if (pending == null || pending.Count == 0)
            {
                return;
            }

            _socket?.RegisterJidAliases(pending, source ?? "bulk-alias");
        }

        /// <summary>
        /// App-state mutation apply ends here so N× ContactAction dual-writes become one
        /// <c>RegisterJidAliases</c> (one Bridge Store log) instead of one Store(1) each.
        /// </summary>
        internal void FlushPendingSocketAliasesAfterAppState(string collectionName) =>
            FlushPendingSocketAliases(
                string.IsNullOrWhiteSpace(collectionName)
                    ? "app-state"
                    : "app-state:" + collectionName);

        private async Task PersistJidAliasesAsync(string reason)
        {
            try
            {
                List<string> chatJids = null;
                await _ui.RunAsync(() =>
                    {
                        chatJids = Chats
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                            .Select(c => NormalizeJid(c.JID))
                            .Where(j => !string.IsNullOrWhiteSpace(j))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    });

                if (chatJids == null || chatJids.Count == 0)
                {
                    return;
                }

                var aliasSnapshot = JidAlias.Snapshot();
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot, chatJids);
                Debug.WriteLine($"[WhatsAppService] Persisted {aliasSnapshot.Count} alias entries immediately ({reason})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist aliases immediately ({reason}): {ex.Message}");
            }
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

        /// <summary>
        /// Records a LID/phone pair and schedules the work that follows from it.
        /// </summary>
        /// <remarks>
        /// The follow-up is scheduled rather than run because it is the same work no matter how
        /// many pairs arrive: rewriting the alias file, collapsing duplicate rows, asking for the
        /// avatars a resolved pair unblocks. One message registering one pair and a history chunk
        /// registering a thousand both end in a single pass now. It used to be a pass each, which
        /// on a first sync meant thousands of dispatches to the UI thread and thousands of writes
        /// of the whole alias map - during the one minute the app has the least to spare.
        /// </remarks>
        internal void RegisterAliasMapping(string lidJid, string pnJid, string source) =>
            TryRecordAliasMapping(lidJid, pnJid, source);

        /// <summary>
        /// Same for a whole set at once, for the sources that deal in tables rather than in single
        /// pairs - a history chunk, a group listing. Follow-up is scheduled by ContactFacade on
        /// each <c>NotifyJidAliasResolved</c> (debounced).
        /// </summary>
        void IJidAliasPersistence.RegisterAliasMappings(
            IEnumerable<KeyValuePair<string, string>> lidToPn,
            string source) =>
            RegisterAliasMappings(lidToPn, source);

        internal void RegisterAliasMappings(IEnumerable<KeyValuePair<string, string>> lidToPn, string source)
        {
            if (lidToPn == null)
            {
                return;
            }

            int changed = 0;
            using (JidAlias.BeginBulkImport())
            {
                foreach (var pair in lidToPn)
                {
                    if (TryRecordAliasMapping(pair.Key, pair.Value, source))
                    {
                        changed++;
                    }
                }
            }

            FlushPendingSocketAliases(source);

            if (changed > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Recorded {changed} new alias pair(s) from {source}");
            }
        }

        /// <summary>
        /// Whether filing this pair would put a contact under our own identity.
        /// </summary>
        /// <remarks>
        /// The live path refuses such a pair in <see cref="TryRecordAliasMapping"/>, but persisted
        /// aliases are written straight into the table on startup, so a bad pair that ever reached
        /// disk came back on every launch and merged that contact's conversation into the self chat.
        ///
        /// This has to be asked *before* the entry is inserted. The guard inside
        /// <c>JidAliasTable.GetCanonicalJid</c> asks afterwards, and by then
        /// <see cref="IsSelfLinkedJid"/> reads the poisoned entry itself as evidence that the
        /// contact is us, which switches that guard off.
        /// </remarks>
        /// <summary>
        /// The bookkeeping half: validates the pair, files it both ways, and reports whether it
        /// told us anything we did not already know. No UI, no disk, no scans.
        /// </summary>
        private bool TryRecordAliasMapping(string lidJid, string pnJid, string source) =>
            TryRecordAliasMappingDispatch.Run(this, lidJid, pnJid, source);

        // --- ITryRecordAliasMappingHost --------------------------------------

        string ITryRecordAliasMappingHost.NormalizeJid(string jid) => NormalizeJid(jid);

        bool ITryRecordAliasMappingHost.IsWellFormedAliasPair(string lid, string pn) =>
            AliasPairPolicy.IsWellFormedPair(lid, pn, JidAlias);

        bool ITryRecordAliasMappingHost.WouldPutContactUnderOurIdentity(string lid, string pn) =>
            AliasPairPolicy.WouldPutAContactUnderOurIdentity(lid, pn, JidAlias);

        void ITryRecordAliasMappingHost.LogSuspiciousAliasSkipped(
            string source,
            string lid,
            string pn) =>
            Debug.WriteLine(
                $"[WhatsAppService] Skipping suspicious alias from {source}: {lid} -> {pn}");

        bool ITryRecordAliasMappingHost.FileAliasBothWays(string lid, string pn) =>
            AliasPairFiling.FileBothWays(JidAlias, lid, pn);

        bool ITryRecordAliasMappingHost.IsBulkImportingAliases =>
            JidAlias.IsBulkImporting;

        void ITryRecordAliasMappingHost.QueuePendingSocketAlias(string lid, string pn) =>
            QueuePendingSocketAlias(lid, pn);

        void ITryRecordAliasMappingHost.RegisterSocketAlias(
            string lid,
            string pn,
            string source) =>
            RegisterSocketAlias(lid, pn, source);

        void ITryRecordAliasMappingHost.NotifyJidAliasResolved(
            string pn,
            string lid,
            string source) =>
            RaiseReport(
                () => Contacts?.NotifyJidAliasResolved(pn, lid, source),
                nameof(IContactService.NotifyJidAliasResolved));

        Task IJidAliasPersistence.PersistAliasesAsync(string reason) => PersistJidAliasesAsync(reason);

    }
}
