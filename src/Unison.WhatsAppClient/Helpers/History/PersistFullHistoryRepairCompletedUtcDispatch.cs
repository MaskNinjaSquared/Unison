// =============================================================================
// PersistFullHistoryRepairCompletedUtcDispatch
//
// Body of PersistFullHistoryRepairCompletedUtc. Local settings I/O on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersistFullHistoryRepairCompletedUtcDispatch
    {
        public static void Run(
            IPersistFullHistoryRepairCompletedUtcHost host,
            DateTime timestampUtc)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.SetLastFullHistoryRepairCompletedUtc(timestampUtc);
            try
            {
                host.WriteLastFullHistoryRepairCompletedUtc(timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                host.LogPersistFullHistoryRepairCompletedFailed(ex.Message);
            }
        }
    }
}
