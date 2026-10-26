// =============================================================================
// IFlushPendingSocketAliasesHost
//
// Side effects for FlushPendingSocketAliasesDispatch.
// =============================================================================
using System.Collections.Generic;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFlushPendingSocketAliasesHost
    {
        bool IsBulkImportingAliases { get; }

        IReadOnlyDictionary<string, string> TakePendingSocketAliasesOrNull();

        void RegisterSocketJidAliases(IReadOnlyDictionary<string, string> pending, string source);
    }
}
