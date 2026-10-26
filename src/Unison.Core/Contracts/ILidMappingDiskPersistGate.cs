// =============================================================================
// ILidMappingDiskPersistGate
//
// First history sync owns SQLite; LID pairs stay in memory until this gate
// opens. HistoryFacade releases after quiet-finalize (IsCompleted) or when warm
// decides there is no first-sync hold.
// =============================================================================
namespace Unison.Core.Contracts
{
    /// <summary>
    /// Opens durable LID mapping writes after history is no longer competing for disk.
    /// </summary>
    public interface ILidMappingDiskPersistGate
    {
        /// <summary>Allows paced disk flushes; flushes any debt accumulated while held.</summary>
        void ReleaseAfterHistory(string reason);
    }
}
