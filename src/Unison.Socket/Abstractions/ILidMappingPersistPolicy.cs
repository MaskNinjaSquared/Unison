// =============================================================================
// ILidMappingPersistPolicy
//
// How many LID pairs reach disk in one write, how long a sub-batch may sit in
// memory first, and how large the in-process LRU may grow. Numbers come from
// the host (SyncWorkloadStrategy per memory tier); Socket must not reference
// Core.
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

        /// <summary>
        /// Max keys in the in-memory LRU (each PN↔LID pair uses two keys). Cap after
        /// which least-recently-touched entries drop. &lt;= 0 → store default (1024).
        /// </summary>
        int MemoryCacheMaxEntries { get; }

        /// <summary>
        /// Forward pairs to seed from disk at session start. Always 0 in production
        /// tiers: no warm on boot; fill only on demand. High differs by a larger
        /// <see cref="MemoryCacheMaxEntries"/> only.
        /// </summary>
        int WarmMaxPairs { get; }
    }
}
