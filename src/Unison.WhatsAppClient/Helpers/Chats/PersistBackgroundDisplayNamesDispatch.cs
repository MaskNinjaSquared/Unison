// =============================================================================
// PersistBackgroundDisplayNamesDispatch
//
// Body of PersistBackgroundDisplayNamesAsync. Table build / broker store on host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistBackgroundDisplayNamesDispatch
    {
        public static async Task RunAsync(IPersistBackgroundDisplayNamesHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                var displayNames = host.BuildBackgroundDisplayNamesTable();
                await host.SaveBackgroundDisplayNamesAsync(
                    displayNames,
                    host.BackgroundSelfMeId,
                    host.BackgroundSelfMeLid).ConfigureAwait(false);
                host.WriteBackgroundDisplayNamesPersisted(displayNames.Count);
            }
            catch (Exception nameSnapshotError)
            {
                host.RecordBackgroundDisplayNamesFailed(nameSnapshotError);
            }
        }
    }
}
