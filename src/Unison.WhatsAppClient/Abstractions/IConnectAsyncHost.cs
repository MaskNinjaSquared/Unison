// =============================================================================
// IConnectAsyncHost
//
// Side effects for ConnectAsyncDispatch.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IConnectAsyncHost : IConnectionConnectGateSource
    {
        Task EnterConnectLockAsync();

        void ExitConnectLock();

        void WriteConnectSkippedAlreadyHealthy();

        void WriteConnectAbortedFatal();

        void WriteConnectForceFreshForQr();

        void TraceForceFreshPairingForQr();

        Task<ConnectionConnectPrepareResult> PrepareConnectAsync();

        void WriteConnectPrepareAbort(ConnectionConnectPrepareResult result);

        void LogConnectAuthState();

        bool ComputeReuseLoadedKeyStore();

        Task OpenConnectAsync(bool reuseLoadedKeyState);
    }
}
