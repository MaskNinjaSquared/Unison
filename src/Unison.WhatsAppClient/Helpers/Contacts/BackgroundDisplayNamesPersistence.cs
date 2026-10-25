// =============================================================================
// BackgroundDisplayNamesPersistence
//
// Owns IPersistBackgroundDisplayNamesHost. Table build + broker save ports;
// BackgroundDisplayNameStore stays on the UWP client (InternalsVisibleTo).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class BackgroundDisplayNamesPersistence : IPersistBackgroundDisplayNamesHost
    {
        private readonly ChatStateStore _chats;
        private readonly JidAliasTable _aliases;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly Func<IDictionary<string, string>, string, string, Task> _saveAsync;
        private readonly IRuntimeDiagnostics _diagnostics;

        public BackgroundDisplayNamesPersistence(
            ChatStateStore chats,
            JidAliasTable aliases,
            Func<string> meId,
            Func<string> meLid,
            Func<IDictionary<string, string>, string, string, Task> saveAsync,
            IRuntimeDiagnostics diagnostics = null)
        {
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
            _meId = meId ?? throw new ArgumentNullException(nameof(meId));
            _meLid = meLid ?? throw new ArgumentNullException(nameof(meLid));
            _saveAsync = saveAsync ?? throw new ArgumentNullException(nameof(saveAsync));
            _diagnostics = diagnostics;
        }

        public Task PersistAsync() =>
            PersistBackgroundDisplayNamesDispatch.RunAsync(this);

        IReadOnlyDictionary<string, string> IPersistBackgroundDisplayNamesHost.BuildBackgroundDisplayNamesTable() =>
            BackgroundDisplayNameTable.Build(
                _chats.Chats.ToList(),
                _chats.PushNames.ToList(),
                _chats.AddressBookNames.ToList(),
                _aliases.ToList());

        string IPersistBackgroundDisplayNamesHost.BackgroundSelfMeId => _meId();

        string IPersistBackgroundDisplayNamesHost.BackgroundSelfMeLid => _meLid();

        Task IPersistBackgroundDisplayNamesHost.SaveBackgroundDisplayNamesAsync(
            IReadOnlyDictionary<string, string> displayNames,
            string meId,
            string meLid)
        {
            Dictionary<string, string> map = displayNames as Dictionary<string, string>;
            if (map == null)
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (displayNames != null)
                {
                    foreach (KeyValuePair<string, string> pair in displayNames)
                    {
                        map[pair.Key] = pair.Value;
                    }
                }
            }

            return _saveAsync(map, meId, meLid);
        }

        void IPersistBackgroundDisplayNamesHost.WriteBackgroundDisplayNamesPersisted(int count)
        {
            try
            {
                _diagnostics?.Write(
                    "socket-broker",
                    "display-name-snapshot-persisted",
                    "count=" + count);
            }
            catch
            {
            }
        }

        void IPersistBackgroundDisplayNamesHost.RecordBackgroundDisplayNamesFailed(Exception error)
        {
            try
            {
                _diagnostics?.RecordException(
                    "socket-broker",
                    "display-name-snapshot-failed",
                    error);
            }
            catch
            {
            }
        }
    }
}
