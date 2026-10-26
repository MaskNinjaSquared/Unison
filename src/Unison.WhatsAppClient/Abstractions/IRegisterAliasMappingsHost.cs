// =============================================================================
// IRegisterAliasMappingsHost
//
// Side effects for RegisterAliasMappingsDispatch.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IRegisterAliasMappingsHost
    {
        IDisposable BeginAliasBulkImport();

        bool TryRecordAliasMapping(string lidJid, string pnJid, string source);

        void FlushPendingSocketAliases(string source);

        void LogRecordedNewAliasPairs(int changed, string source);
    }
}
