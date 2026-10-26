// =============================================================================
// JidAliasPersistence
//
// Owns IJidAliasPersistence + the four alias dispatch hosts that used to live on
// WhatsAppService. Socket registration stays on ISocketAliasRegistrar; table /
// pending-queue / SQLite flush live here (same shape as UsyncAliasHost).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class JidAliasPersistence :
        IJidAliasPersistence,
        ITryRecordAliasMappingHost,
        IRegisterAliasMappingsHost,
        IPersistJidAliasesHost,
        IFlushPendingSocketAliasesHost
    {
        private readonly JidAliasTable _aliases;
        private readonly ISocketAliasRegistrar _socket;
        private readonly IMessageStore _messages;
        private readonly ChatStateStore _chats;
        private readonly IDispatcher _ui;
        private readonly Func<IContactService> _contacts;
        private readonly object _pendingSocketAliasGate = new object();
        private Dictionary<string, string> _pendingSocketAliases;

        public JidAliasPersistence(
            JidAliasTable aliases,
            ISocketAliasRegistrar socket,
            IMessageStore messages,
            ChatStateStore chats,
            IDispatcher ui,
            Func<IContactService> contacts)
        {
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        }

        /// <inheritdoc />
        public void RegisterAliasMappings(
            IEnumerable<KeyValuePair<string, string>> lidToPn,
            string source) =>
            RegisterAliasMappingsDispatch.Run(this, lidToPn, source);

        /// <inheritdoc />
        public Task PersistAliasesAsync(string reason) =>
            PersistJidAliasesDispatch.RunAsync(this, reason);

        // --- IRegisterAliasMappingsHost --------------------------------------

        IDisposable IRegisterAliasMappingsHost.BeginAliasBulkImport() =>
            _aliases.BeginBulkImport();

        bool IRegisterAliasMappingsHost.TryRecordAliasMapping(
            string lidJid,
            string pnJid,
            string source) =>
            TryRecordAliasMappingDispatch.Run(this, lidJid, pnJid, source);

        void IRegisterAliasMappingsHost.FlushPendingSocketAliases(string source) =>
            FlushPendingSocketAliasesDispatch.Run(this, source);

        void IRegisterAliasMappingsHost.LogRecordedNewAliasPairs(int changed, string source) =>
            Debug.WriteLine(
                "[JidAliasPersistence] Recorded " + changed + " new alias pair(s) from " + source);

        // --- ITryRecordAliasMappingHost --------------------------------------

        string ITryRecordAliasMappingHost.NormalizeJid(string jid) =>
            JidHelper.Normalize(jid);

        bool ITryRecordAliasMappingHost.IsWellFormedAliasPair(string lid, string pn) =>
            AliasPairPolicy.IsWellFormedPair(lid, pn, _aliases);

        bool ITryRecordAliasMappingHost.WouldPutContactUnderOurIdentity(string lid, string pn) =>
            AliasPairPolicy.WouldPutAContactUnderOurIdentity(lid, pn, _aliases);

        void ITryRecordAliasMappingHost.LogSuspiciousAliasSkipped(
            string source,
            string lid,
            string pn) =>
            Debug.WriteLine(
                "[JidAliasPersistence] Skipping suspicious alias from " + source + ": " +
                lid + " -> " + pn);

        bool ITryRecordAliasMappingHost.FileAliasBothWays(string lid, string pn) =>
            AliasPairFiling.FileBothWays(_aliases, lid, pn);

        bool ITryRecordAliasMappingHost.IsBulkImportingAliases =>
            _aliases.IsBulkImporting;

        void ITryRecordAliasMappingHost.QueuePendingSocketAlias(string lid, string pn)
        {
            if (string.IsNullOrWhiteSpace(lid) || string.IsNullOrWhiteSpace(pn))
            {
                return;
            }

            lock (_pendingSocketAliasGate)
            {
                if (_pendingSocketAliases == null)
                {
                    _pendingSocketAliases = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                }

                _pendingSocketAliases[lid] = pn;
                _pendingSocketAliases[pn] = lid;
            }
        }

        void ITryRecordAliasMappingHost.RegisterSocketAlias(
            string lid,
            string pn,
            string source) =>
            _socket.RegisterPair(lid, pn, source);

        void ITryRecordAliasMappingHost.NotifyJidAliasResolved(
            string pn,
            string lid,
            string source)
        {
            try
            {
                _contacts()?.NotifyJidAliasResolved(pn, lid, source);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[JidAliasPersistence] NotifyJidAliasResolved failed: " + ex.Message);
            }
        }

        // --- IFlushPendingSocketAliasesHost ----------------------------------

        bool IFlushPendingSocketAliasesHost.IsBulkImportingAliases =>
            _aliases.IsBulkImporting;

        IReadOnlyDictionary<string, string> IFlushPendingSocketAliasesHost.TakePendingSocketAliasesOrNull()
        {
            Dictionary<string, string> pending;
            lock (_pendingSocketAliasGate)
            {
                pending = _pendingSocketAliases;
                _pendingSocketAliases = null;
            }

            return pending;
        }

        void IFlushPendingSocketAliasesHost.RegisterSocketJidAliases(
            IReadOnlyDictionary<string, string> pending,
            string source) =>
            _socket.RegisterPairs(pending, source);

        // --- IPersistJidAliasesHost ------------------------------------------

        async Task<IReadOnlyList<string>> IPersistJidAliasesHost.CollectDistinctChatJidsAsync()
        {
            List<string> chatJids = null;
            await _ui.RunAsync(() =>
            {
                chatJids = _chats.Chats
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                    .Select(c => JidHelper.Normalize(c.JID))
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }).ConfigureAwait(false);
            return chatJids ?? (IReadOnlyList<string>)Array.Empty<string>();
        }

        IReadOnlyDictionary<string, string> IPersistJidAliasesHost.SnapshotJidAliases() =>
            _aliases.Snapshot();

        Task IPersistJidAliasesHost.SaveJidAliasesAsync(
            IReadOnlyDictionary<string, string> aliases,
            IReadOnlyList<string> chatJids)
        {
            Dictionary<string, string> map = aliases as Dictionary<string, string>;
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (aliases != null)
                {
                    foreach (KeyValuePair<string, string> pair in aliases)
                    {
                        map[pair.Key] = pair.Value;
                    }
                }
            }

            return _messages.SaveJidAliasesAsync(map, chatJids);
        }

        void IPersistJidAliasesHost.LogPersistedJidAliases(int count, string reason) =>
            Debug.WriteLine(
                "[JidAliasPersistence] Persisted " + count +
                " alias entries immediately (" + reason + ")");

        void IPersistJidAliasesHost.LogPersistJidAliasesFailed(string reason, string message) =>
            Debug.WriteLine(
                "[JidAliasPersistence] Failed to persist aliases immediately (" +
                reason + "): " + message);
    }
}
