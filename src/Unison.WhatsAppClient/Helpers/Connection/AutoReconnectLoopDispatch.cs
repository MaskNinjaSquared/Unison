// =============================================================================
// AutoReconnectLoopDispatch
//
// Body of AutoReconnectLoopAsync: soft-reconnect gate loop. Connect / health /
// delay I/O stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AutoReconnectLoopDispatch
    {
        public static async Task RunAsync(IAutoReconnectLoopHost host, string trigger)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            int attempt = 0;
            Exception lastError = null;

            while (ConnectionSoftReconnectGates.ShouldContinueLoop(
                       host.SuppressReconnect,
                       host.FatalSessionEnded))
            {
                if (ConnectionSoftReconnectGates.ShouldExitForPairing(host.PairingRestartPending))
                {
                    host.TracePairing(
                        "AutoReconnectLoop EXIT — pairingRestartPending (trigger=" +
                        (trigger ?? string.Empty) + ")");
                    return;
                }

                try
                {
                    if (await host.IsCurrentSocketHealthyAsync().ConfigureAwait(false))
                    {
                        host.LogAutoReconnect(
                            "Reconnect loop found a healthy socket (" + trigger + ")");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                await host.InitializeAsync().ConfigureAwait(false);
                if (ConnectionSoftReconnectGates.ShouldStopBecauseSessionNotRegistered(
                        host.IsAuthStatePresent,
                        host.IsAuthRegistered,
                        host.FatalSessionEnded))
                {
                    host.LogAutoReconnect(
                        "Reconnect loop stopped because the session is not registered");
                    return;
                }

                if (ConnectionSoftReconnectGates.ShouldExitForPairing(host.PairingRestartPending))
                {
                    host.TracePairing(
                        "AutoReconnectLoop EXIT before delay — pairing claimed stage2");
                    return;
                }

                TimeSpan delay = host.ReconnectDelayForAttempt(attempt);
                host.PublishReconnectingStatus();
                host.LogAutoReconnect(
                    "Reconnect attempt " + (attempt + 1) + " in " +
                    delay.TotalSeconds.ToString("F0") + "s (trigger=" + trigger + ")");

                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    if (ConnectionSoftReconnectGates.ShouldAbortAfterDelay(
                            host.SuppressReconnect,
                            host.FatalSessionEnded,
                            host.PairingRestartPending))
                    {
                        if (host.PairingRestartPending)
                        {
                            host.TracePairing(
                                "AutoReconnectLoop EXIT after delay — pairing claimed stage2");
                        }

                        return;
                    }

                    await host.ConnectAsync().ConfigureAwait(false);
                    if (host.FatalSessionEnded)
                    {
                        return;
                    }

                    if (await host.IsCurrentSocketHealthyAsync().ConfigureAwait(false))
                    {
                        host.LogAutoReconnect(
                            "Reconnect succeeded on attempt " + (attempt + 1));
                        return;
                    }

                    lastError = new InvalidOperationException(
                        "Connection completed without a healthy WhatsApp socket");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    host.LogAutoReconnect(
                        "Reconnect attempt " + (attempt + 1) + " failed: " + ex.Message);
                }

                attempt++;
            }

            if (ConnectionSoftReconnectGates.ShouldRaiseReconnectLoopError(
                    lastError != null,
                    host.SuppressReconnect,
                    host.FatalSessionEnded))
            {
                host.RaiseReconnectLoopError(lastError);
            }
        }
    }
}
