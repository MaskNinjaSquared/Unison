// =============================================================================
// ILidMappingPersistPolicy
//
// How many LID pairs reach disk in one write, and how long a sub-batch may sit
// in memory first. Numbers come from the host (SyncWorkloadStrategy per memory
// tier); Socket must not reference Core.
//
// BatchSize or DelayMs <= 0 means write-through (immediate SetAsync), matching
// the store's behaviour when no policy is supplied.
// =============================================================================
namespace Unison.Socket.Abstractions
{
    public interface ILidMappingPersistPolicy
    {
        /// <summary>Pairs (PN user → LID user) per disk flush when write-behind is on.</summary>
        int BatchSize { get; }

        /// <summary>Quiet ms before a sub-batch flush; host arms the timer.</summary>
        int DelayMs { get; }
    }
}
