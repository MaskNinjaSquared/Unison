// =============================================================================
// SyncWorkloadLidMappingPersistPolicy
//
// Adapts SyncWorkloadStrategy tier numbers into the Socket persist policy.
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
        }

        public int BatchSize { get; }

        public int DelayMs { get; }
    }
}
