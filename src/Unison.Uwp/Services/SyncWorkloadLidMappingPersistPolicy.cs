// =============================================================================
// SyncWorkloadLidMappingPersistPolicy
//
// Adapts product tuning into the Socket persist / memory policy (disk batches,
// LRU size, optional post-boot warm window).
// =============================================================================
using System;
using Unison.Core.Contracts.Tuning;
using Unison.Socket.Abstractions;

namespace Unison.Uwp.Services
{
    public sealed class SyncWorkloadLidMappingPersistPolicy : ILidMappingPersistPolicy
    {
        public SyncWorkloadLidMappingPersistPolicy(ILidMappingPersistTuning tuning)
        {
            if (tuning == null)
            {
                throw new ArgumentNullException(nameof(tuning));
            }

            BatchSize = tuning.BatchSize;
            DelayMs = tuning.DelayMs;
            MemoryCacheMaxEntries = tuning.MemoryCacheMaxEntries;
            WarmMaxPairs = tuning.WarmMaxPairs;
        }

        public int BatchSize { get; }

        public int DelayMs { get; }

        public int MemoryCacheMaxEntries { get; }

        public int WarmMaxPairs { get; }
    }
}
