// =============================================================================
// ConnectionPairingStage2
//
// After close code 515: wait for stage-1 socket teardown, ConnectAsync again,
// soft-reconnect if registered credentials survived a failed stage-2.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Orchestrates pairing stage-2 reconnect against <see cref="IConnectionPairingStage2Host"/>.</summary>
    public static class ConnectionPairingStage2
    {
        public static readonly int Stage1CloseWaitMs = 1000;

        public static async Task RunAsync(IConnectionPairingStage2Host host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (host.ShouldAbortPairingRestart)
            {
                host.ClearPairingRestartPending();
                host.ClearReconnectingFlag();
                host.TracePairing("ReconnectForPairingAsync aborted (suppress/fatal)");
                return;
            }

            bool needsPersistentRetry = false;
            try
            {
                host.TracePairing("ReconnectForPairingAsync waiting 1s then ConnectAsync…");
                host.LogPairingReset();
                await Task.Delay(Stage1CloseWaitMs).ConfigureAwait(false);
                await host.ConnectAsync().ConfigureAwait(false);
                host.TracePairing(
                    "ReconnectForPairingAsync ConnectAsync returned status=" +
                    (host.CurrentConnectionStatusOrNull ?? "(null)") +
                    " registered=" + host.IsRegistered);
            }
            catch (Exception ex)
            {
                host.TracePairing("ReconnectForPairingAsync FAILED: " + ex.Message);
                host.RaisePairingError(ex);
                needsPersistentRetry = host.IsRegistered;
                // Stage 2 failed — allow normal reconnect / revoked detection again.
                host.ClearPairingRestartPending();
            }
            finally
            {
                host.ClearReconnectingFlag();
            }

            if (needsPersistentRetry && host.CanScheduleSoftReconnect)
            {
                host.RequestSoftReconnect("pairing-stage2-failed");
            }
        }
    }
}
