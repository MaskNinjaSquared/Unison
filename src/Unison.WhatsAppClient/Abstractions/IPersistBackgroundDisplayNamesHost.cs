// =============================================================================
// IPersistBackgroundDisplayNamesHost
//
// Side effects for PersistBackgroundDisplayNamesDispatch.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistBackgroundDisplayNamesHost
    {
        IReadOnlyDictionary<string, string> BuildBackgroundDisplayNamesTable();

        string BackgroundSelfMeId { get; }

        string BackgroundSelfMeLid { get; }

        Task SaveBackgroundDisplayNamesAsync(
            IReadOnlyDictionary<string, string> displayNames,
            string meId,
            string meLid);

        void WriteBackgroundDisplayNamesPersisted(int count);

        void RecordBackgroundDisplayNamesFailed(Exception error);
    }
}
