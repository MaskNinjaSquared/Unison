// =============================================================================
// IIncomingPumpDrainHost
//
// Side effects for IncomingPumpDrainDispatch. The host holds the typed queue
// item (UWP DecryptedMessageEventArgs); the dispatch only sees ids / offline.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IIncomingPumpDrainHost
    {
        /// <summary>Live-message handle timeout (offline items are not timed).</summary>
        int LiveTimeoutMilliseconds { get; }

        /// <summary>Takes the next item into the host's current slot. False when idle/retired.</summary>
        bool TryTakeNext(int generation, DateTime utcNow);

        bool IsCurrentOffline { get; }

        string CurrentMessageId { get; }

        string CurrentStage { get; }

        Task HandleCurrentAsync();

        bool ShouldRetryTimedOutCurrent();

        void ResetPump(string reason, bool requeueCurrent);

        void RestartPumpIfNeeded();

        void ForgetTimeoutCurrent();

        void FinishCurrent(int generation, DateTime utcNow);

        void NotifyMessageApplied();

        void LogLiveTimeout(string messageId, string stage, int timeoutMs);

        void ObserveLateHandleFault(Task handleTask, string messageId);

        void LogPumpError(Exception ex, string messageId, bool offline, string stage);
    }
}
