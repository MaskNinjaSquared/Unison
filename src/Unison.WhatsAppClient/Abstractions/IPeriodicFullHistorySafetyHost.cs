// =============================================================================
// IPeriodicFullHistorySafetyHost
//
// Side effects for PeriodicFullHistorySafetyDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPeriodicFullHistorySafetyHost
    {
        bool IsSocketReadyForPeriodicFullSafety { get; }

        bool IsHistoryCatchUpActive { get; }

        bool IsFullHistoryClaimed { get; }

        /// <summary>
        /// When false, periodic safety must not re-solicit FULL_HISTORY after stagnant catch-up.
        /// </summary>
        bool AllowStagnantHistoryRepairCascade { get; }

        bool TryReadForceHistoryRepairPending();

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleDetail);

        void LogPeriodicFullSafety(string message);

        void WritePeriodicFullSafetyJournal(string reason);

        Task<bool> RequestFullHistoryOnDemandTrackedAsync(string reason, bool isFreshnessRepair);
    }
}
