using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IBoundMutationGate, IPendingMessageFlushHost
    {

        void IIncomingMessageEnqueueHost.EnqueueRecoveredDecryptedMessage(
            string fromJid,
            string participant,
            string messageId,
            Message message,
            DateTime timestampUtc,
            bool isFromMe,
            string pushName)
        {
            EnqueueDecryptedMessage(new DecryptedMessageEventArgs
            {
                FromJid = fromJid,
                Participant = participant,
                MessageId = messageId,
                Message = message,
                Timestamp = timestampUtc,
                IsFromMe = isFromMe,
                PushName = pushName,
                VerifiedName = null
            });
        }

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

        private void EnqueueDecryptedMessage(Client.DecryptedMessageEventArgs message)
        {
            if (message == null)
            {
                return;
            }

            _incoming.Enqueue(message);
            RestartIncomingMessagePumpIfNeeded();
        }

        private Client.DecryptedMessageEventArgs _pumpCurrent;

        private void RestartIncomingMessagePumpIfNeeded()
        {
            int generation;
            if (!_incoming.TryStart(
                    started => Task.Run(() => IncomingPumpDrainDispatch.RunAsync(this, started)),
                    DateTime.UtcNow,
                    out generation))
            {
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-start",
                "generation=" + generation);
        }

        private void SetIncomingMessagePumpStage(string stage, Client.DecryptedMessageEventArgs message = null)
        {
            _incoming.SetStage(stage, message, DateTime.UtcNow);
        }

        private void ResetIncomingMessagePump(string reason, bool requeueCurrent)
        {
            IncomingQueueReset reset = _incoming.Reset(reason, requeueCurrent, DateTime.UtcNow);

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-reset",
                "reason=" + reason + "; generation=" + reset.Generation +
                "; requeued=" + requeueCurrent + "; qLive=" + reset.LiveDepth + "; qOffline=" + reset.OfflineDepth);
        }

        private bool IsIncomingMessagePumpStalled(TimeSpan limit)
        {
            return _incoming.IsStalled(limit, DateTime.UtcNow);
        }

        int IIncomingPumpDrainHost.LiveTimeoutMilliseconds =>
            IncomingPumpDrainDispatch.DefaultLiveTimeoutMilliseconds;

        bool IIncomingPumpDrainHost.TryTakeNext(int generation, DateTime utcNow)
        {
            Client.DecryptedMessageEventArgs next;
            if (!_incoming.TryTakeNext(generation, utcNow, out next))
            {
                _pumpCurrent = null;
                return false;
            }

            _pumpCurrent = next;
            return true;
        }

        bool IIncomingPumpDrainHost.IsCurrentOffline =>
            _pumpCurrent != null && _pumpCurrent.IsOffline;

        string IIncomingPumpDrainHost.CurrentMessageId =>
            _pumpCurrent?.MessageId;

        string IIncomingPumpDrainHost.CurrentStage => _incoming.Stage;

        Task IIncomingPumpDrainHost.HandleCurrentAsync() =>
            HandleDecryptedMessageAsync(_pumpCurrent);

        bool IIncomingPumpDrainHost.ShouldRetryTimedOutCurrent() =>
            _incoming.ShouldRetryTimedOut(_pumpCurrent);

        void IIncomingPumpDrainHost.ResetPump(string reason, bool requeueCurrent) =>
            ResetIncomingMessagePump(reason, requeueCurrent);

        void IIncomingPumpDrainHost.RestartPumpIfNeeded() =>
            RestartIncomingMessagePumpIfNeeded();

        void IIncomingPumpDrainHost.ForgetTimeoutCurrent() =>
            _incoming.ForgetTimeout(_pumpCurrent);

        void IIncomingPumpDrainHost.FinishCurrent(int generation, DateTime utcNow)
        {
            _incoming.Finish(generation, _pumpCurrent, utcNow);
            _pumpCurrent = null;
        }

        void IIncomingPumpDrainHost.NotifyMessageApplied()
        {
            Interlocked.Increment(ref _diagnosticsAppliedMessageCount);
            Interlocked.Exchange(
                ref _diagnosticsLastAppliedMessageUtcTicks,
                DateTime.UtcNow.Ticks);
        }

        void IIncomingPumpDrainHost.LogLiveTimeout(
            string messageId,
            string stage,
            int timeoutMs) =>
            RuntimeDiagnosticsService.Instance.Write(
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
                        RuntimeDiagnosticsService.Instance.RecordException(
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
            Debug.WriteLine("[WhatsAppService] Incoming message pump error: " + ex.Message);
            RuntimeDiagnosticsService.Instance.RecordException(
                "messages",
                "incoming-pump-error",
                ex,
                "offline=" + offline +
                "; id=" + (messageId ?? "<none>") +
                "; stage=" + stage);
        }

        private async Task WaitForIncomingMessageQueueDrainAsync(int timeoutMs)
        {
            Task pump = _incoming.PumpTask;
            if (pump.IsCompleted)
            {
                return;
            }

            await Task.WhenAny(pump, Task.Delay(timeoutMs));
        }

        /// <summary>
        /// Handles real-time decrypted messages from SocketClient
        /// </summary>
        private async Task HandleDecryptedMessageAsync(Client.DecryptedMessageEventArgs e)
        {
            // ProcessIncomingMessageQueueAsync is the sole caller and already guarantees
            // one-at-a-time ingestion. A second non-reentrant semaphore here caused a
            // permanent self-deadlock when recovered messages re-entered the pipeline.
            try
            {
                SetIncomingMessagePumpStage("routing", e);
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] HandleDecryptedMessageAsync from {e.FromJid}, participant={e.Participant}, id={e.MessageId}");
                }

                if (Messages == null)
                {
                    Debug.WriteLine("[WhatsAppService] Dropping inbound message: MessageFacade is not attached.");
                    RuntimeDiagnosticsService.Instance.Write(
                        "messages",
                        "message-facade-missing",
                        "messageId=" + (e.MessageId ?? string.Empty));
                    return;
                }

                SetIncomingMessagePumpStage("route", e);
                await Messages.NotifyIncomingDecryptedAsync(new IncomingDecryptedNotifyRequest
                {
                    Message = e.Message,
                    MessageId = e.MessageId,
                    FromJid = e.FromJid,
                    RecipientJid = e.RecipientJid,
                    PeerRecipientPn = e.PeerRecipientPn,
                    PeerRecipientLid = e.PeerRecipientLid,
                    SenderLid = e.SenderLid,
                    ParticipantJid = e.Participant,
                    ParticipantAltJid = e.ParticipantAlt,
                    VerifiedName = e.VerifiedName,
                    PushName = e.PushName,
                    Timestamp = e.Timestamp,
                    IsOffline = e.IsOffline,
                    IsFromMe = e.IsFromMe,
                    // MessageFacade fills blank SelfDisplayName from SelfProfileState.
                    SelfDisplayName = null
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] HandleDecryptedMessageAsync error: {ex.Message}");
                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "handle-decrypted-failed",
                    ex,
                    "id=" + (e?.MessageId ?? "<none>") + "; stage=" + _incoming.Stage);
                throw;
            }
        }

        void IPendingMessageFlushHost.ApplyFlushAction(PendingFlushAction action) =>
            OfflineReplayFlushDispatch.ApplyAction(this, action);

        Task IPendingMessageFlushHost.FlushOfflineReplayAsync(string reason) =>
            FlushOfflineReplayMessagesAsync(reason);

        bool IOfflineReplayHost.IsActiveChat(string canonicalJid) => IsActiveChatJid(canonicalJid);

        bool IOfflineReplayHost.IsWindowVisible => Unison.Uwp.App.IsWindowVisible;

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
