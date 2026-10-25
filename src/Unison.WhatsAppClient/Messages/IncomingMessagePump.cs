// =============================================================================
// IncomingMessagePump
//
// Owns the live/offline decrypted queue, drain Task.Run, and current item.
// WhatsAppService only enqueues from the wire and asks for stall/reset/drain
// on health / suspend / resume — handle path goes to IMessageService.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Proto;

namespace Unison.WhatsAppClient.Messages
{
    public sealed class IncomingMessagePump :
        IIncomingPumpDrainHost,
        IHandleDecryptedMessageHost,
        IIncomingMessageEnqueueHost
    {
        private readonly IncomingMessageQueue<IncomingDecryptedNotifyRequest> _queue =
            new IncomingMessageQueue<IncomingDecryptedNotifyRequest>(
                message => message.IsOffline,
                message => message.MessageId);

        private readonly Func<IMessageService> _resolveMessages;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action _onMessageApplied;

        private IncomingDecryptedNotifyRequest _current;
        private long _appliedCount;

        public IncomingMessagePump(
            Func<IMessageService> resolveMessages,
            IRuntimeDiagnostics diagnostics = null,
            Action onMessageApplied = null)
        {
            _resolveMessages = resolveMessages
                ?? throw new ArgumentNullException(nameof(resolveMessages));
            _diagnostics = diagnostics;
            _onMessageApplied = onMessageApplied;
        }

        public long AppliedCount => Interlocked.Read(ref _appliedCount);

        public string Stage => _queue.Stage;

        public IncomingQueueSnapshot Snapshot() => _queue.Snapshot();

        public void Enqueue(IncomingDecryptedNotifyRequest message)
        {
            if (message == null)
            {
                return;
            }

            _queue.Enqueue(message);
            RestartIfNeeded();
        }

        public void EnqueueRecoveredDecryptedMessage(
            string fromJid,
            string participant,
            string messageId,
            Message message,
            DateTime timestampUtc,
            bool isFromMe,
            string pushName)
        {
            Enqueue(
                IncomingDecryptedNotifyMap.FromPump(
                    message,
                    messageId,
                    fromJid,
                    recipientJid: null,
                    peerRecipientPn: null,
                    peerRecipientLid: null,
                    senderLid: null,
                    participant,
                    participantAltJid: null,
                    verifiedName: null,
                    pushName,
                    timestampUtc,
                    isOffline: false,
                    isFromMe));
        }

        void IIncomingMessageEnqueueHost.EnqueueRecoveredDecryptedMessage(
            string fromJid,
            string participant,
            string messageId,
            Message message,
            DateTime timestampUtc,
            bool isFromMe,
            string pushName) =>
            EnqueueRecoveredDecryptedMessage(
                fromJid,
                participant,
                messageId,
                message,
                timestampUtc,
                isFromMe,
                pushName);

        public void RestartIfNeeded()
        {
            int generation;
            if (!_queue.TryStart(
                    started => Task.Run(() => IncomingPumpDrainDispatch.RunAsync(this, started)),
                    DateTime.UtcNow,
                    out generation))
            {
                return;
            }

            _diagnostics?.Write(
                "messages",
                "incoming-pump-start",
                "generation=" + generation);
        }

        public void Reset(string reason, bool requeueCurrent)
        {
            IncomingQueueReset reset = _queue.Reset(reason, requeueCurrent, DateTime.UtcNow);

            _diagnostics?.Write(
                "messages",
                "incoming-pump-reset",
                "reason=" + reason + "; generation=" + reset.Generation +
                "; requeued=" + requeueCurrent + "; qLive=" + reset.LiveDepth +
                "; qOffline=" + reset.OfflineDepth);
        }

        public bool IsStalled(TimeSpan limit) =>
            _queue.IsStalled(limit, DateTime.UtcNow);

        public async Task WaitForDrainAsync(int timeoutMs)
        {
            Task pump = _queue.PumpTask;
            if (pump.IsCompleted)
            {
                return;
            }

            await Task.WhenAny(pump, Task.Delay(timeoutMs)).ConfigureAwait(false);
        }

        // --- IIncomingPumpDrainHost ----------------------------------------

        int IIncomingPumpDrainHost.LiveTimeoutMilliseconds =>
            IncomingPumpDrainDispatch.DefaultLiveTimeoutMilliseconds;

        bool IIncomingPumpDrainHost.TryTakeNext(int generation, DateTime utcNow)
        {
            IncomingDecryptedNotifyRequest next;
            if (!_queue.TryTakeNext(generation, utcNow, out next))
            {
                _current = null;
                return false;
            }

            _current = next;
            return true;
        }

        bool IIncomingPumpDrainHost.IsCurrentOffline =>
            _current != null && _current.IsOffline;

        string IIncomingPumpDrainHost.CurrentMessageId =>
            _current?.MessageId;

        string IIncomingPumpDrainHost.CurrentStage => _queue.Stage;

        Task IIncomingPumpDrainHost.HandleCurrentAsync() =>
            HandleDecryptedMessageDispatch.RunAsync(this, _current);

        bool IIncomingPumpDrainHost.ShouldRetryTimedOutCurrent() =>
            _queue.ShouldRetryTimedOut(_current);

        void IIncomingPumpDrainHost.ResetPump(string reason, bool requeueCurrent) =>
            Reset(reason, requeueCurrent);

        void IIncomingPumpDrainHost.RestartPumpIfNeeded() =>
            RestartIfNeeded();

        void IIncomingPumpDrainHost.ForgetTimeoutCurrent() =>
            _queue.ForgetTimeout(_current);

        void IIncomingPumpDrainHost.FinishCurrent(int generation, DateTime utcNow)
        {
            _queue.Finish(generation, _current, utcNow);
            _current = null;
        }

        void IIncomingPumpDrainHost.NotifyMessageApplied()
        {
            Interlocked.Increment(ref _appliedCount);
            Interlocked.Exchange(
                ref _lastAppliedUtcTicks,
                DateTime.UtcNow.Ticks);
            _onMessageApplied?.Invoke();
        }

        private long _lastAppliedUtcTicks;

        public long LastAppliedUtcTicks =>
            Interlocked.Read(ref _lastAppliedUtcTicks);

        void IIncomingPumpDrainHost.LogLiveTimeout(
            string messageId,
            string stage,
            int timeoutMs) =>
            _diagnostics?.Write(
                "messages",
                "incoming-message-timeout",
                "id=" + (messageId ?? "<none>") +
                "; stage=" + stage +
                "; timeoutMs=" + timeoutMs);

        void IIncomingPumpDrainHost.ObserveLateHandleFault(Task handleTask, string messageId)
        {
            if (handleTask == null)
            {
                return;
            }

            _ = handleTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        _diagnostics?.RecordException(
                            "messages",
                            "late-message-fault",
                            t.Exception,
                            "id=" + (messageId ?? "<none>"));
                    }
                },
                TaskScheduler.Default);
        }

        void IIncomingPumpDrainHost.LogPumpError(
            Exception ex,
            string messageId,
            bool offline,
            string stage)
        {
            Debug.WriteLine("[IncomingMessagePump] error: " + (ex != null ? ex.Message : string.Empty));
            _diagnostics?.RecordException(
                "messages",
                "incoming-pump-error",
                ex,
                "offline=" + offline +
                "; id=" + (messageId ?? "<none>") +
                "; stage=" + stage);
        }

        // --- IHandleDecryptedMessageHost -----------------------------------

        void IHandleDecryptedMessageHost.SetIncomingMessagePumpStage(
            string stage,
            IncomingDecryptedNotifyRequest request) =>
            _queue.SetStage(stage, request, DateTime.UtcNow);

        void IHandleDecryptedMessageHost.LogLiveInboundHandle(
            IncomingDecryptedNotifyRequest request) =>
            Debug.WriteLine(
                "[IncomingMessagePump] HandleDecrypted from " + request.FromJid +
                ", participant=" + request.ParticipantJid +
                ", id=" + request.MessageId);

        bool IHandleDecryptedMessageHost.HasMessageFacade =>
            _resolveMessages() != null;

        void IHandleDecryptedMessageHost.WriteMessageFacadeMissing(string messageId)
        {
            Debug.WriteLine("[IncomingMessagePump] Dropping inbound: MessageFacade is not attached.");
            _diagnostics?.Write(
                "messages",
                "message-facade-missing",
                "messageId=" + (messageId ?? string.Empty));
        }

        Task IHandleDecryptedMessageHost.NotifyIncomingDecryptedAsync(
            IncomingDecryptedNotifyRequest request)
        {
            IMessageService messages = _resolveMessages();
            if (messages == null)
            {
                return Task.CompletedTask;
            }

            return messages.NotifyIncomingDecryptedAsync(request);
        }

        void IHandleDecryptedMessageHost.LogHandleDecryptedError(
            Exception error,
            IncomingDecryptedNotifyRequest request) =>
            Debug.WriteLine(
                "[IncomingMessagePump] HandleDecrypted error: " +
                (error != null ? error.Message : string.Empty));

        void IHandleDecryptedMessageHost.RecordHandleDecryptedFailed(
            Exception error,
            IncomingDecryptedNotifyRequest request) =>
            _diagnostics?.RecordException(
                "messages",
                "handle-decrypted-failed",
                error,
                "id=" + (request?.MessageId ?? "<none>") + "; stage=" + _queue.Stage);

        string IHandleDecryptedMessageHost.IncomingPumpStage => _queue.Stage;
    }
}
