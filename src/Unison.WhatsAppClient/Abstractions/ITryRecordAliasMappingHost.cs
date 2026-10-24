// =============================================================================
// ITryRecordAliasMappingHost
//
// Side effects for TryRecordAliasMappingDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ITryRecordAliasMappingHost
    {
        string NormalizeJid(string jid);

        bool IsWellFormedAliasPair(string lid, string pn);

        bool WouldPutContactUnderOurIdentity(string lid, string pn);

        void LogSuspiciousAliasSkipped(string source, string lid, string pn);

        bool FileAliasBothWays(string lid, string pn);

        bool IsBulkImportingAliases { get; }

        void QueuePendingSocketAlias(string lid, string pn);

        void RegisterSocketAlias(string lid, string pn, string source);

        void NotifyJidAliasResolved(string pn, string lid, string source);
    }
}
