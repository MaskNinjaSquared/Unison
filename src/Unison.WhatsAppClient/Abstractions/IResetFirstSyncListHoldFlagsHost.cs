// =============================================================================
// IResetFirstSyncListHoldFlagsHost
//
// Side effects for ResetFirstSyncListHoldFlagsDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IResetFirstSyncListHoldFlagsHost
    {
        void ClearFirstSyncListHoldFlags();

        void LogFirstSyncListHoldFlagsReset(string reason);
    }
}
