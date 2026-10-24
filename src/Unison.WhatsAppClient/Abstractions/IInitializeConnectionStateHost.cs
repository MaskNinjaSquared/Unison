// =============================================================================
// IInitializeConnectionStateHost
//
// Coarse steps for InitializeConnectionStateDispatch (inside init lock).
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IInitializeConnectionStateHost
    {
        bool HasAuthState { get; }

        Task InitializeMessageStoreAsync();

        void LoadHistoryFreshnessRepairState();

        Task RecoverPendingIncomingJournalAsync();

        Task LoadOrCreateAuthStateAsync();

        void ApplyLinkedAccountReconnectToastGate();

        Task WarmLidMappingCacheAsync();

        Task RestorePersistedJidAliasesAsync();

        void RegisterSelfIdentitySocketAlias();

        void SyncSelfProfileFromAuth();
    }
}
