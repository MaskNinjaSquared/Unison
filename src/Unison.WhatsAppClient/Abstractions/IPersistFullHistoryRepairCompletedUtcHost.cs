// =============================================================================
// IPersistFullHistoryRepairCompletedUtcHost
//
// Side effects for PersistFullHistoryRepairCompletedUtcDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPersistFullHistoryRepairCompletedUtcHost
    {
        void SetLastFullHistoryRepairCompletedUtc(DateTime timestampUtc);

        void WriteLastFullHistoryRepairCompletedUtc(string roundtripText);

        void LogPersistFullHistoryRepairCompletedFailed(string message);
    }
}
