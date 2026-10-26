using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Proto;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Thin host ports for placeholder resend and bound mutation. The decrypted
    /// queue/drain live on <see cref="IncomingMessagePump"/>; offline-replay flush
    /// ownership is <see cref="OfflineReplayFlush"/>.
    /// </summary>
    public partial class WhatsAppService : IBoundMutationGate
    {
        void IIncomingMessageEnqueueHost.EnqueueRecoveredDecryptedMessage(
            string fromJid,
            string participant,
            string messageId,
            Message message,
            DateTime timestampUtc,
            bool isFromMe,
            string pushName) =>
            _incomingPump.EnqueueRecoveredDecryptedMessage(
                fromJid,
                participant,
                messageId,
                message,
                timestampUtc,
                isFromMe,
                pushName);

        bool IPlaceholderResendSocket.IsHandshakeComplete =>
            _socket != null && _socket.IsHandshakeComplete;

        string IPlaceholderResendSocket.GenerateMessageId() => _socket.GenerateMessageId();

        Task<string> IPlaceholderResendSocket.RequestPlaceholderResendAsync(Proto.MessageKey key, string stanzaId) =>
            _socket.RequestPlaceholderResendAsync(key, stanzaId);

        async Task IBoundMutationGate.RunAsync(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (_ui.Current != null)
            {
                await _ui.RunAsync(action).ConfigureAwait(false);
            }
            else
            {
                action();
            }
        }

        private void EnqueueDecryptedMessage(IncomingDecryptedNotifyRequest message) =>
            _incomingPump.Enqueue(message);

        private void RestartIncomingMessagePumpIfNeeded() =>
            _incomingPump.RestartIfNeeded();

        private void ResetIncomingMessagePump(string reason, bool requeueCurrent) =>
            _incomingPump.Reset(reason, requeueCurrent);

        private bool IsIncomingMessagePumpStalled(TimeSpan limit) =>
            _incomingPump.IsStalled(limit);

        private Task WaitForIncomingMessageQueueDrainAsync(int timeoutMs) =>
            _incomingPump.WaitForDrainAsync(timeoutMs);

        /// <summary>Stage string for connection-health stall diagnostics.</summary>
        internal string IncomingPumpStage =>
            _incomingPump != null ? _incomingPump.Stage : "idle";

        bool IOfflineReplayHost.IsActiveChat(string canonicalJid) => IsActiveChatJid(canonicalJid);

        bool IOfflineReplayHost.IsWindowVisible => IsAppWindowVisible;

        void IOfflineReplayHost.NoteCatchUpProgress(string reason) => NoteFullHistoryCatchUpProgress(reason);

        void IOfflineReplayHost.SchedulePersist() => SchedulePersist();

        void IOfflineReplayHost.ReportFlushFailure(string reason, Exception error)
        {
            RuntimeDiagnosticsService.Instance.RecordException(
                "messages",
                "offline-summary-apply-failed",
                error,
                "reason=" + reason);
        }

        void IOfflineReplayHost.CompleteFirstOfflineRelease(string reason)
        {
            PublishConnectionUpdate("synced");
            EnableScheduledPersist("offline completion (" + reason + ")");
            _ = TryConsumeMessageStoreForceHistoryRepairAsync(reason);
        }
    }
}
