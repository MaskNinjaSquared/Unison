// =============================================================================
// IEnrichmentTuning
//
// Batch sizes and pauses for post-sync enrichment (avatars, names, group roster /
// metadata). Values come from the product layer; the client only executes them.
// =============================================================================
using System;

namespace Unison.Core.Contracts.Tuning
{
    public interface IEnrichmentTuning
    {
        int AvatarFetchBatchSize { get; }

        int AvatarFetchInterRequestDelayMs { get; }

        int NameResolveMaxPerPass { get; }

        int NameResolveChunkSize { get; }

        int NameResolveInterChunkDelayMs { get; }

        int GroupRosterBatchSize { get; }

        int GroupRosterInterRequestDelayMs { get; }

        int GroupRosterNextBatchDelaySeconds { get; }

        int GroupMetadataBatchSize { get; }

        int GroupMetadataPauseMs { get; }

        int GroupListingResumeSettleMs { get; }

        /// <summary>Roster rows built per chunk while applying group metadata.</summary>
        int GroupRosterBuildChunkSize { get; }

        /// <summary>Debounce for member-event UI flushes; null keeps the default.</summary>
        TimeSpan? GroupRosterFlushDebounce { get; }

        TimeSpan DeferredAvatarRetryDelay { get; }

        int DeferredAvatarMaxRetries { get; }
    }
}
