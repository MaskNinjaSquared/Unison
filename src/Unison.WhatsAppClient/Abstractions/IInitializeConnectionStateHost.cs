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

        /// <summary>Journal a named init step (Mobile boot hang diagnosis).</summary>
        void NoteConnectionStateInitStep(string step);

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
