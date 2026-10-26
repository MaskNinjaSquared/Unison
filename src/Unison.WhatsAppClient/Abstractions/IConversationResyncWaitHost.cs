// =============================================================================
// IConversationResyncWaitHost
//
// Wait half of ResyncConversationsAsync after wipe (FULL_HISTORY request +
// history wait). Wipe / progress reporting stay on the client entry.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// Named distinctly from Core's conversation-resync wipe host on the client.
    /// </summary>
    public interface IConversationResyncWaitHost
    {
        void LogConversationResync(string message);

        void RaiseConversationResyncStatus(string status);

        void ArmUserResyncHistoryWait(TaskCompletionSource<bool> historyDone);

        Task<bool> EnsureFullHistoryRequestedForUserResyncAsync(string reason);

        void CompleteUserResyncHistoryWaitFailed(TaskCompletionSource<bool> historyDone);

        TimeSpan HistoryWaitTimeout { get; }

        void ClearUserResyncHistoryWait(TaskCompletionSource<bool> historyDone);
    }
}
