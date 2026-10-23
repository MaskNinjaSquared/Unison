// =============================================================================
// ConnectionHealthMonitor
//
// Background health loop: pump stall recovery, probe / stall evaluate, optional
// defer for usync / FULL_HISTORY, then force-reconnect. Policy thresholds live
// in ConnectionHealthPolicy; side effects stay on the host.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Runs the connection health loop against <see cref="IConnectionHealthMonitorHost"/>.</summary>
    public static class ConnectionHealthMonitor
    {
        /// <summary>
        /// Defer-aware loop (default): skip force-reconnect while usync is held or
        /// history catch-up is active, unless the ordered node queue itself stalled.
        /// </summary>
        public static Task RunDeferAwareAsync(
            IConnectionHealthMonitorHost host,
            CancellationToken token)
        {
            return RunAsync(host, token, deferAware: true, structureLabel: "current-backup");
        }

        /// <summary>
        /// 1.8.3 loop: probe / stall / force-reconnect with no usync / catch-up defer.
        /// </summary>
        public static Task RunLegacy183Async(
            IConnectionHealthMonitorHost host,
            CancellationToken token)
        {
            return RunAsync(host, token, deferAware: false, structureLabel: "1.8.3");
        }

        private static async Task RunAsync(
            IConnectionHealthMonitorHost host,
            CancellationToken token,
            bool deferAware,
            string structureLabel)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                while (!token.IsCancellationRequested && !host.ShouldAbortLoop)
                {
                    await host.DelayCheckIntervalAsync(token).ConfigureAwait(false);
                    if (token.IsCancellationRequested ||
                        host.ShouldAbortLoop ||
                        !host.IsMonitoredSocketCurrent)
                    {
                        return;
                    }

                    if (!host.IsMonitoredSocketConnected)
                    {
                        host.LogHealth(
                            "Health monitor found a disconnected socket; deferring to close handler");
                        return;
                    }

                    host.RestartIncomingPumpIfStalled();

                    bool stalled = host.IsNodeProcessingStalled;
                    ConnectionHealthAction action = ConnectionHealthPolicy.Evaluate(
                        stalled,
                        host.HasFreshBackgroundConnection);
                    if (action == ConnectionHealthAction.Idle)
                    {
                        continue;
                    }

                    if (deferAware)
                    {
                        string deferReason = ConnectionHealthPolicy.DeferForceReconnectReason(
                            stalled,
                            host.IsUsyncGateHeld,
                            host.IsHistoryCatchUpActive);
                        if (deferReason != null)
                        {
                            host.LogHealth("Health monitor deferred (" + deferReason + ")");
                            host.WriteHealthJournal(
                                "health-defer-" + deferReason,
                                "pendingIq=" + host.PendingQueryCount);
                            continue;
                        }
                    }

                    bool healthy = false;
                    if (action == ConnectionHealthAction.Probe)
                    {
                        healthy = await host.ProbeBackgroundAsync().ConfigureAwait(false);
                    }

                    if (healthy)
                    {
                        continue;
                    }

                    if (host.ShouldAbortLoop)
                    {
                        return;
                    }

                    if (!host.IsMonitoredSocketConnected)
                    {
                        host.LogHealth(
                            "Health probe failed on a closed socket; deferring to close handler");
                        return;
                    }

                    if (deferAware)
                    {
                        string deferAfterProbe = ConnectionHealthPolicy.DeferForceReconnectReason(
                            stalled,
                            host.IsUsyncGateHeld,
                            host.IsHistoryCatchUpActive);
                        if (deferAfterProbe != null)
                        {
                            host.LogHealth(
                                "Health monitor deferred after probe (" + deferAfterProbe + ")");
                            host.WriteHealthJournal(
                                "health-defer-" + deferAfterProbe,
                                "pendingIq=" + host.PendingQueryCount + "; afterProbe=True");
                            continue;
                        }
                    }

                    string reason = stalled
                        ? "node-queue-stalled:" + host.QueuedNodeProcessingCount
                        : "socket-no-inbound-response";
                    host.LogHealth("Health monitor forcing reconnect: " + reason);
                    host.WriteHealthJournal(
                        "health-force-reconnect",
                        reason +
                        "; nodeQueue=" + host.QueuedNodeProcessingCount +
                        "; pendingIq=" + host.PendingQueryCount +
                        "; structure=" + structureLabel);

                    host.ForceDisconnectMonitoredSocket();

                    if (!host.ShouldAbortLoop)
                    {
                        host.RequestSoftReconnect(reason);
                    }

                    return;
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                host.LogHealth("Connection health monitor failed: " + ex.Message);
                host.RecordHealthException(ex);

                string fatalCode = host.TryGetExplicitLogoutStreamCode(ex);
                if (!string.IsNullOrEmpty(fatalCode))
                {
                    host.LatchFatalAndReportStream(fatalCode);
                    return;
                }

                if (!host.ShouldAbortLoop &&
                    host.IsMonitoredSocketCurrent &&
                    host.IsMonitoredSocketConnected)
                {
                    host.RequestSoftReconnect("health-monitor-error");
                }
            }
        }
    }
}
