// =============================================================================
// TryStartPairingStage2ReconnectDispatch
//
// Body of TryStartPairingStage2Reconnect. Lock / Task.Run stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class TryStartPairingStage2ReconnectDispatch
    {
        public static void Run(ITryStartPairingStage2ReconnectHost host, string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (ConnectionSoftReconnectGates.ShouldSkipPairingStage2Start(
                    host.SuppressReconnect,
                    host.FatalSessionEnded))
            {
                host.TracePairingStage2(
                    "stage2-reconnect skipped (suppress/fatal) reason=" + (reason ?? string.Empty));
                return;
            }

            host.MarkPairingRestartPending(reason);

            if (!host.TryClaimReconnectSlot())
            {
                host.TracePairingStage2(
                    "stage2-reconnect already in-flight reason=" + (reason ?? string.Empty) +
                    " — leaving ownership to current reconnect (pairingRestartPending=true blocks generic loop)");
                return;
            }

            host.StopConnectionHealthMonitor("pairing-restart");
            host.TracePairingStage2(
                "stage2-reconnect START reason=" + (reason ?? string.Empty));
            host.FireAndForgetPairingStage2Reconnect();
        }
    }
}
