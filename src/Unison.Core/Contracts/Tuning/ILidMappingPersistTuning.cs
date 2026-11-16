// =============================================================================
// ILidMappingPersistTuning
//
// Write-behind and in-memory LRU knobs for LID mapping persistence. Mirrors
// Socket's ILidMappingPersistPolicy shape; the UWP adapter copies into policy.
// =============================================================================
namespace Unison.Core.Contracts.Tuning
{
    public interface ILidMappingPersistTuning
    {
        int BatchSize { get; }

        int DelayMs { get; }

        /// <summary>LRU key budget (PN↔LID pair uses two keys).</summary>
        int MemoryCacheMaxEntries { get; }

        /// <summary>Forward pairs to seed from disk at session start (0 in production).</summary>
        int WarmMaxPairs { get; }
    }
}