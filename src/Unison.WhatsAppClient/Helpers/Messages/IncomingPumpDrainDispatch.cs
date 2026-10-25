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
        /// <summary>Production default for <see cref="IIncomingPumpDrainHost.LiveTimeoutMilliseconds"/>.</summary>
        public const int DefaultLiveTimeoutMilliseconds = 12000;

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

                        Task completed = await Task.WhenAny(
                                handleTask,
                                Task.Delay(timeoutMs))
                            .ConfigureAwait(false);

                        if (completed != handleTask)
                        {
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
                                return;
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
    }
}