// =============================================================================
// TryGetHistoryStaleReason
//
// Probe shared by history owners: true (with a reason) when the newest stored
// tip looks older than the freshness threshold. The client answers it.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public delegate bool TryGetHistoryStaleReason(DateTime nowUtc, out string staleReason);
}
