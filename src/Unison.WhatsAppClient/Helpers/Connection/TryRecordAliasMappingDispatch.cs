// =============================================================================
// TryRecordAliasMappingDispatch
//
// Body of TryRecordAliasMapping. Alias table / socket stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class TryRecordAliasMappingDispatch
    {
        public static bool Run(
            ITryRecordAliasMappingHost host,
            string lidJid,
            string pnJid,
            string source)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            string lid = host.NormalizeJid(lidJid);
            string pn = host.NormalizeJid(pnJid);
            if (!host.IsWellFormedAliasPair(lid, pn))
            {
                return false;
            }

            if (host.WouldPutContactUnderOurIdentity(lid, pn))
            {
                host.LogSuspiciousAliasSkipped(source, lid, pn);
                return false;
            }

            bool changed = host.FileAliasBothWays(lid, pn);
            if (!changed)
            {
                // Live traffic re-states the same pair on every message. Recognising that costs a
                // dictionary lookup and saves everything below — including a Bridge Store(1).
                return false;
            }

            if (host.IsBulkImportingAliases)
            {
                host.QueuePendingSocketAlias(lid, pn);
            }
            else
            {
                host.RegisterSocketAlias(lid, pn, source);
            }

            host.NotifyJidAliasResolved(pn, lid, source);
            return true;
        }
    }
}
