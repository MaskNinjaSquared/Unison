// =============================================================================
// IPersistJidAliasesHost
//
// Side effects for PersistJidAliasesDispatch.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistJidAliasesHost
    {
        Task<IReadOnlyList<string>> CollectDistinctChatJidsAsync();

        IReadOnlyDictionary<string, string> SnapshotJidAliases();

        Task SaveJidAliasesAsync(
            IReadOnlyDictionary<string, string> aliases,
            IReadOnlyList<string> chatJids);

        void LogPersistedJidAliases(int count, string reason);

        void LogPersistJidAliasesFailed(string reason, string message);
    }
}
