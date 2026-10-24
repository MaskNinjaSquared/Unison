// =============================================================================
// ConnectAsyncDispatch
//
// Body of ConnectAsync. Lock / prepare / open I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ConnectAsyncDispatch
    {
        public static async Task RunAsync(IConnectAsyncHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            // Serialize connection attempts. More than one lifecycle callback can fire
            // during launch/resume on Windows 10 Mobile. The old implementation let each
            // caller replace a socket that had just become healthy, producing connection
            // storms and stale send/receive tasks.
            await host.EnterConnectLockAsync().ConfigureAwait(false);
            try
            {
                // Early gates (healthy skip / fatal abort / QR force-fresh journal) live in
                // ConnectionConnectGates — opening the transport stays below.
                ConnectionConnectGate gate = ConnectionConnectGates.Decide(host);
                if (gate == ConnectionConnectGate.SkipAlreadyHealthy)
                {
                    host.WriteConnectSkippedAlreadyHealthy();
                    return;
                }

                if (gate == ConnectionConnectGate.AbortFatalSession)
                {
                    host.WriteConnectAbortedFatal();
                    return;
                }

                if (ConnectionConnectGates.ShouldJournalForceFreshForQr(host))
                {
                    host.WriteConnectForceFreshForQr();
                    host.TraceForceFreshPairingForQr();
                }

                // Drop previous socket + load auth. Transport build stays below.
                ConnectionConnectPrepareResult prepare =
                    await host.PrepareConnectAsync().ConfigureAwait(false);
                if (prepare == ConnectionConnectPrepareResult.AbortSessionWipeInFlight ||
                    prepare == ConnectionConnectPrepareResult.AbortFatalAfterAuth)
                {
                    host.WriteConnectPrepareAbort(prepare);
                    return;
                }

                host.LogConnectAuthState();
                bool reuseLoadedKeyState = host.ComputeReuseLoadedKeyStore();
                await host.OpenConnectAsync(reuseLoadedKeyState).ConfigureAwait(false);
            }
            finally
            {
                host.ExitConnectLock();
            }
        }
    }
}
