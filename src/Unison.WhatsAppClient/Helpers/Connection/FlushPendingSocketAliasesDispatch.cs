// =============================================================================
// FlushPendingSocketAliasesDispatch
//
// Body of FlushPendingSocketAliases. Pending queue / socket stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class FlushPendingSocketAliasesDispatch
    {
        public static void Run(IFlushPendingSocketAliasesHost host, string source)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.IsBulkImportingAliases)
            {
                return;
            }

            IReadOnlyDictionary<string, string> pending = host.TakePendingSocketAliasesOrNull();
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            host.RegisterSocketJidAliases(pending, source ?? "bulk-alias");
        }
    }
}
