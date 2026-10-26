// =============================================================================
// IRaiseSessionClearedHost
//
// Side effects for RaiseSessionClearedDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IRaiseSessionClearedHost
    {
        void ResetFirstSyncListHoldFlags(string reason);

        void NotifyHistorySessionCleared();

        void LogSessionCleared(string message);

        Task RaiseOnSessionClearedOnUiAsync(bool startPairing);
    }
}
