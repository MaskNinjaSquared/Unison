// =============================================================================
// ConversationResyncWaitDispatch
//
// Post-wipe half of ResyncConversationsAsync: request FULL_HISTORY and wait
// (or time out). Wipe / progress phases stay on the client entry.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ConversationResyncWaitDispatch
    {
        public static async Task RunAsync(IConversationResyncWaitHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            host.RaiseConversationResyncStatus("Re-syncing conversations...");

            var historyDone = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            host.ArmUserResyncHistoryWait(historyDone);

            bool requested = await host.EnsureFullHistoryRequestedForUserResyncAsync(
                    "user-resync-conversations")
                .ConfigureAwait(false);

            if (!requested)
            {
                host.LogConversationResync(
                    "Full history request could not be sent after wipe/reconnect.");
                host.RaiseConversationResyncStatus(
                    "Could not start history download. Try again.");
                host.CompleteUserResyncHistoryWaitFailed(historyDone);
                host.LogConversationResync(
                    "Resync conversations finished (request failed).");
                return;
            }

            host.RaiseConversationResyncStatus("Preparing conversations…");

            try
            {
                Task finished = await Task.WhenAny(
                        historyDone.Task,
                        Task.Delay(host.HistoryWaitTimeout))
                    .ConfigureAwait(false);
                if (UserResyncFullHistoryGates.HistoryWaitTimedOut(finished, historyDone.Task))
                {
                    host.LogConversationResync(
                        "Resync conversations timed out waiting for history sync.");
                    historyDone.TrySetResult(false);
                }
            }
            finally
            {
                host.ClearUserResyncHistoryWait(historyDone);
            }

            host.LogConversationResync(
                "Resync conversations finished (history observed or timed out).");
        }
    }
}
