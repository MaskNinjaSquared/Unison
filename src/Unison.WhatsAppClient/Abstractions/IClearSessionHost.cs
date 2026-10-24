// =============================================================================
// IClearSessionHost
//
// Side effects for ClearSessionDispatch.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IClearSessionHost
    {
        void LogClearSession(string message);

        void CaptureKeyStoreBeforeDisconnect();

        void SuppressReconnectToastForClear();

        void ClearReconnectRequiredToast();

        Task ClearAuthStoreEarlyAsync();

        void NullAuthState();

        void ArmFatalDisconnectFlagsForClear();

        void StopConnectionHealthMonitor(string reason);

        void DisconnectAndDisposeSocket();

        void ResetSessionEstablishedSignal();

        Task RaiseSessionClearedAsync(bool startPairing);

        Task YieldAsync();

        Task ClearFileKeyStoreAsync();

        Task ClearAuthStoreIdempotentAsync();

        void DropSharedKeyStoreAndResumeFlags();

        Task ClearNoiseSessionAsync();

        void AllowPairingAfterAuthWipe();

        Task WipeMessageStoreAsync();

        Task ClearGroupRosterAsync();

        Task ClearInMemorySessionStateAsync();

        void ClearNotifications();
    }
}
