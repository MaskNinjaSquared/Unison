// =============================================================================
// SessionOpenOnDemandPendingConsumeDispatch
//
// After connect: re-ask session-open OnDemand for JIDs latched across a crash.
// Clears the latch when consuming starts (reopen still covers a failed ask).
// PairingBootstrap defers without clearing.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SessionOpenOnDemandPendingConsumeDispatch
    {
        public static async Task RunAsync(
            ISessionOpenOnDemandPendingConsumeHost host,
            string reason)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (!HistorySyncFlowDecision.ShouldSolicitPostInitFullHistory(
                    host.IsPairingBootstrapActive))
            {
                int deferred = host.PeekSessionOpenOnDemandPendingCount();
                if (deferred > 0)
                {
                    host.LogSessionOpenOnDemandPending(
                        "SessionOpenOnDemandPending deferred — PairingBootstrap active (" +
                        (reason ?? string.Empty) + "; count=" + deferred + ")");
                }

                return;
            }

            IReadOnlyList<string> pending = host.TakeSessionOpenOnDemandPending();
            if (pending == null || pending.Count == 0)
            {
                return;
            }

            host.LogSessionOpenOnDemandPending(
                "SessionOpenOnDemandPending consuming (" +
                (reason ?? string.Empty) + "; count=" + pending.Count + ")");

            for (int i = 0; i < pending.Count; i++)
            {
                string jid = pending[i];
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                try
                {
                    host.ClaimSessionOpenOnDemand(jid);
                    await host.EnsureHistoryOnDemandAsync(jid).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    host.LogSessionOpenOnDemandPending(
                        "SessionOpenOnDemandPending request failed for " + jid +
                        ": " + ex.Message);
                }
            }
        }
    }
}
