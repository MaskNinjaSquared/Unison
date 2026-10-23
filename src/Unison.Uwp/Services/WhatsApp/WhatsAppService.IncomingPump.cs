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

        private void RegisterMissingMessage(string chatJid, string participant, string messageId, bool isFromMe, DateTime timestamp, string reason)
        {
            string normJid = NormalizeJid(chatJid);
            _missing.Note(normJid, messageId, participant, isFromMe, timestamp, reason, DateTime.UtcNow);

            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            if (!ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine($"[WhatsAppService] Queued missing-message recovery for {messageId} in {normJid} (reason={reason})");
            }
        }

        void IMissingMessageChatHost.ResolveMissingMessage(string chatJid, string messageId, string source) =>
            ResolveMissingMessage(chatJid, messageId, source);

        void IMissingMessageChatHost.RegisterMissingMessage(
            string chatJid,
            string participant,
            string messageId,
            bool isFromMe,
            DateTime timestamp,
            string reason) =>
            RegisterMissingMessage(chatJid, participant, messageId, isFromMe, timestamp, reason);

        Task<bool> IMissingMessageChatHost.RequestPlaceholderResendAsync(
            string chatJid,
            string messageId,
            string trigger) =>
            TryRequestPlaceholderResendAsync(chatJid, messageId, trigger);

        List<MissingMessageCandidate> IMissingMessageChatHost.SelectPlaceholderResendDrain(
            int maxRequests,
            out int totalEligible) =>
            _missing.SelectForDrain(maxRequests, out totalEligible);

        PlaceholderResendRequestState IMissingMessageChatHost.TakePlaceholderPeerResponse(string stanzaId) =>
            _missing.TakePeerResponse(stanzaId);

        bool IMissingMessageChatHost.TryApplyPlaceholderResendAck(
            string ackId,
            bool rejected,
            out PlaceholderResendRequestState requestState) =>
            _missing.TryApplyAck(ackId, rejected, DateTime.UtcNow, out requestState);

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

        private void ResolveMissingMessage(string chatJid, string messageId, string source)
        {
            string normJid = NormalizeJid(chatJid);
            MissingMessageRemoval removal = _missing.Resolve(normJid, messageId);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            if (removal.ScheduledResend != null)
            {
                try
                {
                    removal.ScheduledResend.Cancel();
                    removal.ScheduledResend.Dispose();
                    if (!ShouldDeferReconnectReplayWork())
                    {
                        Debug.WriteLine($"[WhatsAppService] placeholder resend cancelled for {messageId} in {normJid} ({source})");
                    }
                }
                catch
                {
                }
            }

            if (!ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine($"[WhatsAppService] Resolved missing-message recovery for {messageId} in {normJid} via {source}");
            }
        }

        /// <summary>
        /// Drops every pending missing-message repair for a conversation that is going away.
        /// </summary>
        /// <remarks>
        /// Two callers used to do this by removing the key inline — on the UI thread, outside
        /// <c>_missing.Gate</c>, while the pump reads and writes the same dictionary from a
        /// background thread. That is a plain `Dictionary` being mutated from two threads at once.
        /// They also dropped the candidates without cancelling their scheduled resends, so a timer
        /// stayed alive to ask the server for a message belonging to a deleted conversation.
        /// </remarks>
        void IMissingMessageChatHost.ForgetMissingMessagesForChat(string chatJid) =>
            ForgetMissingMessagesForChat(chatJid);

        private void ForgetMissingMessagesForChat(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return;
            }

            _missing.Cooldowns.Remove(normJid);
            List<MissingMessageCandidate> dropped = new List<MissingMessageCandidate>(_missing.ForgetChat(normJid));
            if (dropped.Count == 0)
            {
                return;
            }

            foreach (var candidate in dropped)
            {
                var cts = candidate?.PlaceholderScheduleCts;
                if (cts == null)
                {
                    continue;
                }

                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch
                {
                }
            }
        }

        private bool TryGetMissingMessage(string chatJid, string messageId, out MissingMessageCandidate candidate)
        {
            candidate = null;
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            lock (_missing.Gate)
            {
                return _missing.TryGet(normJid, messageId, out candidate);
            }
        }

        private Task<bool> TryRequestPlaceholderResendAsync(string chatJid, string messageId, string trigger)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return Task.FromResult(false);
            }

            if (!TryGetMissingMessage(chatJid, messageId, out var candidate))
            {
                return Task.FromResult(false);
            }

            if (ShouldDeferPlaceholderResend(trigger, out var deferReason))
            {
                if (!ShouldDeferReconnectReplayWork())
                {
                    Debug.WriteLine($"[WhatsAppService] Deferring placeholder resend for {candidate.MessageId} in {candidate.ChatJid} (trigger={trigger}, reason={deferReason})");
                }
                return Task.FromResult(false);
            }

            Task dispatched = PlaceholderResends.TryDispatch(candidate, chatJid, messageId, trigger);
            return Task.FromResult(dispatched != null);
        }

        private PlaceholderResendDispatch PlaceholderResends =>
            _placeholderResends ??= new PlaceholderResendDispatch(
                _missing,
                this,
                line => Debug.WriteLine("[WhatsAppService] " + line),
                PlaceholderResendDispatchDelay,
                PlaceholderResendResponseTimeout);

        string IPlaceholderResendSocket.GenerateMessageId() => _socket.GenerateMessageId();

        Task<string> IPlaceholderResendSocket.RequestPlaceholderResendAsync(Proto.MessageKey key, string stanzaId) =>
            _socket.RequestPlaceholderResendAsync(key, stanzaId);

        private bool ShouldDeferPlaceholderResend(string trigger, out string reason)
        {
            reason = null;
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            return PlaceholderResendDeferral.IsDeferred(
                ShouldDeferReconnectReplayWork(),
                _socket.IsAwaitingInitialSync,
                _historyRequests.ChatsInFlight > 0,
                out reason);
        }

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

        private void RestartIncomingMessagePumpIfNeeded()
        {
            int generation;
            if (!_incoming.TryStart(
                    started => Task.Run(() => ProcessIncomingMessageQueueAsync(started)),
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

        private async Task ProcessIncomingMessageQueueAsync(int generation)
        {
            while (true)
            {
                // Offline replay records are timestamp-guarded, so serving live traffic
                // first cannot let them overwrite a newer preview.
                Client.DecryptedMessageEventArgs next;
                if (!_incoming.TryTakeNext(generation, DateTime.UtcNow, out next))
                {
                    return;
                }

                try
                {
                    Task handleTask = HandleDecryptedMessageAsync(next);
                    if (!next.IsOffline)
                    {
                        Task completed = await Task.WhenAny(handleTask, Task.Delay(LiveIncomingMessageTimeoutMs));
                        if (completed != handleTask)
                        {
                            RuntimeDiagnosticsService.Instance.Write(
                                "messages",
                                "incoming-message-timeout",
                                "id=" + (next.MessageId ?? "<none>") +
                                "; stage=" + _incoming.Stage +
                                "; timeoutMs=" + LiveIncomingMessageTimeoutMs);

                            _ = handleTask.ContinueWith(
                                t =>
                                {
                                    if (t.IsFaulted)
                                    {
                                        RuntimeDiagnosticsService.Instance.RecordException(
                                            "messages",
                                            "late-message-fault",
                                            t.Exception,
                                            "id=" + (next.MessageId ?? "<none>"));
                                    }
                                },
                                TaskScheduler.Default);

                            bool requeueTimedOutMessage = _incoming.ShouldRetryTimedOut(next);
                            ResetIncomingMessagePump(
                                requeueTimedOutMessage ? "message-timeout-retry" : "message-timeout-skip",
                                requeueCurrent: requeueTimedOutMessage);
                            RestartIncomingMessagePumpIfNeeded();
                            return;
                        }
                    }

                    await handleTask;
                    _incoming.ForgetTimeout(next);
                    Interlocked.Increment(ref _diagnosticsAppliedMessageCount);
                    Interlocked.Exchange(ref _diagnosticsLastAppliedMessageUtcTicks, DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Incoming message pump error: {ex.Message}");
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "messages",
                        "incoming-pump-error",
                        ex,
                        "offline=" + (next != null && next.IsOffline) +
                        "; id=" + (next?.MessageId ?? "<none>") +
                        "; stage=" + _incoming.Stage);
                }
                finally
                {
                    _incoming.Finish(generation, next, DateTime.UtcNow);
                }
            }
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
                    SelfDisplayName = _authState?.Me?.Name
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

        void IPendingMessageFlushHost.ApplyFlushAction(PendingFlushAction action)
        {
            if (action == PendingFlushAction.FlushNow)
            {
                _ = FlushOfflineReplayMessagesAsync("message-batch-threshold");
            }
            else if (action == PendingFlushAction.ScheduleTimer)
            {
                ScheduleOfflineReplayFlushTimer();
            }
        }

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

    }
}
