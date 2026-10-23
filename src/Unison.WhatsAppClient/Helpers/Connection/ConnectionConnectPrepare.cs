// =============================================================================
// ConnectionConnectPrepare
//
// After early ConnectAsync gates: reset latches, drop the previous socket, load
// auth, abort on wipe/fatal, apply registered-session connect state. Building
// the transport stays on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public enum ConnectionConnectPrepareResult
    {
        Proceed,

        AbortSessionWipeInFlight,

        AbortFatalAfterAuth
    }

    /// <summary>Prepare phase of <c>ConnectAsync</c> against <see cref="IConnectionConnectPrepareHost"/>.</summary>
    public static class ConnectionConnectPrepare
    {
        public static async Task<ConnectionConnectPrepareResult> RunAsync(
            IConnectionConnectPrepareHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.ResetPerConnectionGates();
            host.MarkConnecting();
            host.StopHealthAndDropCurrentSocket();
            host.CancelDeferredAvatars();

            await host.EnsureAuthLoadedAsync().ConfigureAwait(false);

            if (!host.HasAuthState)
            {
                return ConnectionConnectPrepareResult.AbortSessionWipeInFlight;
            }

            if (host.ShouldRefuseConnectBecauseSessionDied)
            {
                return ConnectionConnectPrepareResult.AbortFatalAfterAuth;
            }

            host.ApplyRegisteredSessionConnectState();
            return ConnectionConnectPrepareResult.Proceed;
        }

        /// <summary>
        /// Pre-session close may escalate to logout only for returning registered companions.
        /// Fresh QR and 515 pairing stage-2 must not.
        /// </summary>
        public static bool CountPreSessionCloseAsFatal(
            bool authRegistered,
            bool pairingRestartPending)
        {
            return authRegistered && !pairingRestartPending;
        }

        /// <summary>
        /// Same-process resume can reuse an already-populated key store when sessions or
        /// prekeys are already in memory.
        /// </summary>
        public static bool ShouldReuseLoadedKeyStore(
            bool hasSharedKeyStore,
            int sessionCount,
            int preKeyCount)
        {
            return hasSharedKeyStore && (sessionCount > 0 || preKeyCount > 0);
        }
    }
}
