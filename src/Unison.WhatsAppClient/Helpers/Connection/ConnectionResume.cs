// =============================================================================
// ConnectionResume
//
// Foreground resume after suspend/background: clear suppress, consume broker
// reconnect, reclaim or EnsureConnected, soft-reconnect on failure.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Orchestrates app resume against <see cref="IConnectionResumeHost"/>.</summary>
    public static class ConnectionResume
    {
        public static readonly int FastResumeTimeoutMs = 22000;

        public static readonly int NormalResumeTimeoutMs = 35000;

        public static async Task RunAsync(IConnectionResumeHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!await host.IsRegisteredAsync().ConfigureAwait(false))
            {
                return;
            }

            if (host.IsFatalSessionEnded)
            {
                host.WriteResumeJournal("resume-blocked", "reason=fatal-session-ended");
                return;
            }

            host.ClearSuppressReconnect();

            if (await host.ConsumeBrokerReconnectRequestAsync().ConfigureAwait(false))
            {
                host.MarkForceFreshConnectOnResume();
            }

            if (await host.TryReclaimBrokerOwnedSocketAsync().ConfigureAwait(false))
            {
                return;
            }

            bool fastResume = host.ConsumeForceFreshConnectOnResume();
            host.WriteResumeJournal(
                "fast-resume-start",
                "freshTransport=" + fastResume);

            try
            {
                await host.EnsureConnectedAsync(
                        fastResume ? FastResumeTimeoutMs : NormalResumeTimeoutMs,
                        fastResume)
                    .ConfigureAwait(false);
                host.RestartIncomingMessagePumpIfNeeded();
            }
            catch (Exception ex)
            {
                host.RecordResumeException("resume-ensure-failed", ex);
                host.RequestSoftReconnect("resume-fallback");
            }
        }
    }
}
