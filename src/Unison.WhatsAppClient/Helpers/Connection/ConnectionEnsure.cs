// =============================================================================
// ConnectionEnsure
//
// The ensure-connected budget shared by ConnectionFacade and the send host:
// healthy check, optional drop, ConnectAsync with timeout, session-gate wait.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Ensures a usable socket before work that needs the network. Policy only —
    /// the host owns health probe, drop, connect and the session gate.
    /// </summary>
    public static class ConnectionEnsure
    {
        /// <summary>
        /// Runs under <paramref name="gate"/> so resume and send do not race two connects.
        /// </summary>
        public static async Task RunAsync(
            IConnectionEnsureHost host,
            SemaphoreSlim gate,
            int timeoutMs,
            bool forceFreshTransport)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (gate == null)
            {
                throw new ArgumentNullException(nameof(gate));
            }

            if (!forceFreshTransport && await host.IsCurrentSocketHealthyAsync().ConfigureAwait(false))
            {
                return;
            }

            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!forceFreshTransport && await host.IsCurrentSocketHealthyAsync().ConfigureAwait(false))
                {
                    return;
                }

                if (forceFreshTransport)
                {
                    host.DropCurrentSocketForFastResume();
                }

                if (!await host.IsRegisteredAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException("WhatsApp session is not registered");
                }

                Task connectTask = host.ConnectAsync();
                Task connectCompleted = await Task.WhenAny(connectTask, Task.Delay(timeoutMs))
                    .ConfigureAwait(false);
                if (connectCompleted != connectTask)
                {
                    throw new TimeoutException("Timed out reconnecting to WhatsApp");
                }

                await connectTask.ConfigureAwait(false);

                if (!host.IsConnected)
                {
                    await host.WaitForSessionEstablishedAsync(timeoutMs).ConfigureAwait(false);
                }

                if (!host.IsConnected)
                {
                    throw new InvalidOperationException("WhatsApp connection is not ready");
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
