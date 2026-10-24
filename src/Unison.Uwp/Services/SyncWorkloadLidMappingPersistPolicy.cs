// =============================================================================
// SyncWorkloadLidMappingPersistPolicy
//
// Adapts SyncWorkloadStrategy tier numbers into the Socket persist / memory
// policy (disk batches, LRU size, optional post-boot warm window).
// =============================================================================
using System;
using Unison.Core.State;
using Unison.Socket.Abstractions;

namespace Unison.Uwp.Services
{
    public sealed class SyncWorkloadLidMappingPersistPolicy : ILidMappingPersistPolicy
    {
        public SyncWorkloadLidMappingPersistPolicy(SyncWorkloadStrategy workload)
        {
            if (workload == null)
            {
                throw new ArgumentNullException(nameof(workload));
            }

            BatchSize = workload.LidMappingPersistBatchSize;
            DelayMs = workload.LidMappingPersistDelayMs;
            MemoryCacheMaxEntries = workload.LidMappingMemoryCacheMaxEntries;
            WarmMaxPairs = workload.LidMappingWarmMaxPairs;
        }

        public int BatchSize { get; }

        public int DelayMs { get; }

        public int MemoryCacheMaxEntries { get; }

        public int WarmMaxPairs { get; }
    }
}
