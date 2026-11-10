// =============================================================================
// IncomingPumpDrainDispatch
//
// ProcessIncomingMessageQueueAsync body: live-first drain, live timeout, apply
// counters. Queue typing and HandleDecrypted stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public static class IncomingPumpDrainDispatch
    {
        /// <summary>
        /// Production default for <see cref="IIncomingPumpDrainHost.LiveTimeoutMilliseconds"/>.
        /// Matches ConnectionHealthPolicy.IncomingPumpStallLimit (18s) so a stuck
        /// <c>route</c> cannot sit at timeout-skip with health still <c>stalled=False</c>.
        /// </summary>
        public const int DefaultLiveTimeoutMilliseconds = 18000;

        /// <summary>Journal <c>route-slow</c> after this many ms while handle is still running.</summary>
        public const int DefaultRouteSlowWarnMilliseconds = 8000;

        public static async Task RunAsync(IIncomingPumpDrainHost host, int generation)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            while (true)
            {
                // Offline replay records are timestamp-guarded, so serving live traffic
                // first cannot let them overwrite a newer preview.
                if (!host.TryTakeNext(generation, DateTime.UtcNow))
                {
                    return;
                }

                bool offline = host.IsCurrentOffline;
                string messageId = host.CurrentMessageId;

                try
                {
                    Task handleTask = host.HandleCurrentAsync();
                    if (!offline)
                    {
                        int timeoutMs = host.LiveTimeoutMilliseconds > 0
                            ? host.LiveTimeoutMilliseconds
                            : DefaultLiveTimeoutMilliseconds;

                        if (!await WaitForLiveHandleAsync(host, handleTask, messageId, timeoutMs, generation)
                                .ConfigureAwait(false))
                        {
                            return;
                        }
                    }

                    await handleTask.ConfigureAwait(false);
                    host.ForgetTimeoutCurrent();
                    host.NotifyMessageApplied();
                }
                catch (Exception ex)
                {
                    host.LogPumpError(ex, messageId, offline, host.CurrentStage);
                }
                finally
                {
                    host.FinishCurrent(generation, DateTime.UtcNow);
                }
            }
        }

        /// <summary>
        /// Returns false when the drain should stop (timeout reset/restart or stale generation).
        /// Returns true when <paramref name="handleTask"/> completed within the timeout.
        /// </summary>
        private static async Task<bool> WaitForLiveHandleAsync(
            IIncomingPumpDrainHost host,
            Task handleTask,
            string messageId,
            int timeoutMs,
            int generation)
        {
            Task timeoutTask = Task.Delay(timeoutMs);
            int slowMs = host.RouteSlowWarnMilliseconds;
            if (slowMs <= 0)
            {
                slowMs = 0;
            }

            if (slowMs > 0 && slowMs < timeoutMs)
            {
                Task first = await Task.WhenAny(handleTask, Task.Delay(slowMs), timeoutTask)
                    .ConfigureAwait(false);
                if (first == handleTask)
                {
                    return true;
                }

                if (!handleTask.IsCompleted && !timeoutTask.IsCompleted)
                {
                    host.LogRouteSlow(messageId, host.CurrentStage, slowMs);
                }
            }

            if (handleTask.IsCompleted)
            {
                return true;
            }

            Task completed = await Task.WhenAny(handleTask, timeoutTask).ConfigureAwait(false);
            if (completed == handleTask)
            {
                return true;
            }

            host.ObserveLateHandleFault(handleTask, messageId);

            // Health-stall / prior timeout already Reset and started a newer
            // drain; this Delay is leftover. Reset again with _current==null
            // left pumpStage=reset:* and qLive=0 with no restart.
            if (!host.IsDrainGenerationCurrent(generation))
            {
                host.LogLiveTimeout(
                    messageId,
                    "stale-drain:" + host.CurrentStage,
                    timeoutMs);
                return false;
            }

            host.LogLiveTimeout(
                messageId,
                host.CurrentStage,
                timeoutMs);

            bool requeueTimedOutMessage = host.ShouldRetryTimedOutCurrent();
            host.ResetPump(
                requeueTimedOutMessage
                    ? "message-timeout-retry"
                    : "message-timeout-skip",
                requeueCurrent: requeueTimedOutMessage);
            host.RestartPumpIfNeeded();
            return false;
        }
    }
}
