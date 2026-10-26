// =============================================================================
// RegisterAliasMappingsDispatch
//
// Body of RegisterAliasMappings. Bulk scope / alias table stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class RegisterAliasMappingsDispatch
    {
        public static void Run(
            IRegisterAliasMappingsHost host,
            IEnumerable<KeyValuePair<string, string>> lidToPn,
            string source)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (lidToPn == null)
            {
                return;
            }

            int changed = 0;
            using (host.BeginAliasBulkImport())
            {
                foreach (KeyValuePair<string, string> pair in lidToPn)
                {
                    if (host.TryRecordAliasMapping(pair.Key, pair.Value, source))
                    {
                        changed++;
                    }
                }
            }

            host.FlushPendingSocketAliases(source);

            if (changed > 0)
            {
                host.LogRecordedNewAliasPairs(changed, source);
            }
        }
    }
}
