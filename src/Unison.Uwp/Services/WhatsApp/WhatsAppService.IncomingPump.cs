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

        private async Task TryDrainPendingPlaceholderResendsAsync(string trigger, int maxRequests = 4)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return;
            }

            if (ShouldDeferPlaceholderResend(trigger, out var deferReason))
            {
                Debug.WriteLine($"[WhatsAppService] Skipping deferred placeholder resend drain ({trigger}) because {deferReason}");
                return;
            }

            List<MissingMessageCandidate> pending;
            int totalEligible;
            pending = _missing.SelectForDrain(maxRequests, out totalEligible);

            if (pending.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain found no pending messages ({trigger})");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain selected {pending.Count}/{totalEligible} eligible message(s) ({trigger}): {string.Join(" | ", pending.Select(MissingMessageCandidate.FormatForLog))}");

            int requested = 0;
            foreach (var candidate in pending)
            {
                if (ShouldDeferPlaceholderResend(trigger, out deferReason))
                {
                    Debug.WriteLine($"[WhatsAppService] Stopping deferred placeholder resend drain ({trigger}) because {deferReason}");
                    break;
                }

                if (await TryRequestPlaceholderResendAsync(candidate.ChatJid, candidate.MessageId, $"deferred-drain:{trigger}"))
                {
                    requested++;
                }
            }

            Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain requested {requested}/{pending.Count} message(s) ({trigger})");

            if (totalEligible > pending.Count)
            {
                SchedulePendingPlaceholderResendDrain($"follow-up:{trigger}", maxRequests, PlaceholderResendFollowUpDrainDelay);
            }
        }

        private void SchedulePendingPlaceholderResendDrain(string trigger, int maxRequests = 4)
        {
            SchedulePendingPlaceholderResendDrain(trigger, maxRequests, PlaceholderResendDrainDelay);
        }

        private void SchedulePendingPlaceholderResendDrain(string trigger, int maxRequests, TimeSpan delay)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay);
                    await TryDrainPendingPlaceholderResendsAsync(trigger, maxRequests);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain failed ({trigger}): {ex.Message}");
                }
            });
        }

        public async Task<bool> EnsureActiveChatReconciledAsync(string chatJid, int maxRequests = 6)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || _socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            DateTime utcNow = DateTime.UtcNow;
            if (!_missing.TryBeginReconcile(normJid, utcNow, ActiveChatReconcileCooldown))
            {
                return false;
            }

            List<string> pendingIds = _missing.CopyPendingIds(normJid, maxRequests);

            if (pendingIds.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile found no pending missing-message repairs for {normJid}");
                return false;
            }

            bool requestedAny = false;
            foreach (var pendingId in pendingIds)
            {
                requestedAny |= await TryRequestPlaceholderResendAsync(normJid, pendingId, "active-chat");
            }

            if (requestedAny)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile scheduled placeholder resend for {pendingIds.Count} message(s) in {normJid}");
            }
            else if (pendingIds.Count > 0)
            {
                Debug.WriteLine($"[WhatsAppService] Active chat reconcile deferred placeholder resend pressure for {normJid}");
            }

            return requestedAny;
        }


        private async Task RecoverPendingIncomingJournalAsync()
        {
            try
            {
                var pending = await _messageStore.LoadPendingIncomingAsync();
                if (pending == null || pending.Count == 0)
                {
                    return;
                }

                int recovered = 0;
                var latestByChat = new List<KeyValuePair<string, ChatMessage>>();
                foreach (var group in pending
                    .Where(item => item?.Message != null && !string.IsNullOrWhiteSpace(item.ChatJid))
                    .GroupBy(item => NormalizeJid(item.ChatJid), StringComparer.OrdinalIgnoreCase))
                {
                    var messages = group
                        .Select(item => item.Message)
                        .Where(message => message != null)
                        .OrderBy(message => ChatMessageOrder.ToComparableUtc(message.Timestamp))
                        .ToList();
                    if (messages.Count == 0)
                    {
                        continue;
                    }

                    QueueMessagesForPersist(
                        group.Key,
                        messages,
                        queueIncomingJournal: false,
                        scheduleFlush: false);
                    latestByChat.Add(new KeyValuePair<string, ChatMessage>(
                        group.Key,
                        messages[messages.Count - 1]));
                    recovered += messages.Count;
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "messages",
                    "incoming-journal-recovered",
                    "count=" + recovered + "; chats=" + latestByChat.Count);

                // Let the socket acquire storage first. The pending snapshot already
                // makes these messages available if a conversation is opened.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000);
                    try
                    {
                        await FlushOfflineReplayMessagesAsync("incoming-journal-recovery");

                        if (ChatFacade != null)
                        {
                            await ChatFacade.ApplyIncomingJournalRecoveryPreviewsAsync(latestByChat)
                                .ConfigureAwait(false);
                        }

                        RuntimeDiagnosticsService.Instance.Write(
                            "messages",
                            "incoming-journal-recovery-applied",
                            "count=" + recovered + "; chats=" + latestByChat.Count);
                    }
                    catch (Exception ex)
                    {
                        RuntimeDiagnosticsService.Instance.RecordException(
                            "messages",
                            "incoming-journal-recovery-flush-failed",
                            ex);
                    }
                });
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "incoming-journal-recovery-failed",
                    ex);
            }
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

        private async Task ProcessPeerDataOperationResponseAsync(Proto.Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            if (response == null)
            {
                return;
            }

            Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse received: stanzaId={response.StanzaId}, requestType={response.PeerDataOperationRequestType}, resultCount={response.PeerDataOperationResult?.Count ?? 0}");

            HistoryOnDemandRequest historyRequestState = _historyRequests.Find(response.StanzaId);

            foreach (var result in response.PeerDataOperationResult ?? Enumerable.Empty<Proto.Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult>())
            {
                if (result.FullHistorySyncOnDemandRequestResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] FullHistorySyncOnDemand response observed: stanzaId={response.StanzaId}, responseCode={result.FullHistorySyncOnDemandRequestResponse.ResponseCode}, requestMetadataId={result.FullHistorySyncOnDemandRequestResponse.RequestMetadata?.RequestId}");
                }

                if (result.HistorySyncChunkRetryResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] HistorySyncChunkRetry response observed: stanzaId={response.StanzaId}, responseCode={result.HistorySyncChunkRetryResponse.ResponseCode}, canRecover={result.HistorySyncChunkRetryResponse.CanRecover}, requestId={result.HistorySyncChunkRetryResponse.RequestId}");
                }

                if (result.SyncdSnapshotFatalRecoveryResponse != null)
                {
                    Debug.WriteLine($"[WhatsAppService] SyncD fatal recovery response observed: stanzaId={response.StanzaId}, compressed={result.SyncdSnapshotFatalRecoveryResponse.IsCompressed}, bytes={result.SyncdSnapshotFatalRecoveryResponse.CollectionSnapshot?.Length ?? 0}");
                }
            }

            if (Messages != null)
            {
                await Messages.ApplyPeerDataPlaceholderResendsAsync(
                    response.StanzaId,
                    response.PeerDataOperationResult).ConfigureAwait(false);
            }

            if (historyRequestState != null &&
                (response.PeerDataOperationRequestType == Proto.Message.Types.PeerDataOperationRequestType.FullHistorySyncOnDemand ||
                 response.PeerDataOperationRequestType == Proto.Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand))
            {
                Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse completed without immediate history payload: requestType={historyRequestState.TypeName}, stanzaId={response.StanzaId}, chat={historyRequestState.ChatJid ?? "<full-history>"}, baseline={historyRequestState.BaselineMessageCount}, trigger={historyRequestState.TriggerReason ?? "unspecified"}");
            }
        }

        private void HandlePlaceholderResendAckNode(BinaryNode node)
        {
            if (node?.Attrs == null || Messages == null)
            {
                return;
            }

            node.Attrs.TryGetValue("class", out var ackClass);
            node.Attrs.TryGetValue("id", out var ackId);
            node.Attrs.TryGetValue("error", out var ackError);
            Messages.HandlePlaceholderResendAck(ackClass, ackId, ackError);
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

                IncomingEnvelopeKind sessionKind = IncomingEnvelopeDisposition.ClassifySessionControl(e.Message);
                if (sessionKind == IncomingEnvelopeKind.PeerDataOperationResponse)
                {
                    var response = e.Message.ProtocolMessage.PeerDataOperationRequestResponseMessage;
                    await ProcessPeerDataOperationResponseAsync(response).ConfigureAwait(false);
                    return;
                }

                // Both of these are the session's business now: the app state module inside
                // Unison.Socket takes the key share and recovers from a fatal sync itself.
                if (sessionKind == IncomingEnvelopeKind.AppStateSessionOnly)
                {
                    return;
                }

                if (sessionKind == IncomingEnvelopeKind.Placeholder)
                {
                    if (Messages == null)
                    {
                        Debug.WriteLine("[WhatsAppService] Dropping placeholder: MessageFacade is not attached.");
                        return;
                    }

                    string placeholderType = e.Message?.PlaceholderMessage != null
                        ? e.Message.PlaceholderMessage.Type.ToString()
                        : null;
                    await Messages.HandleIncomingPlaceholderAsync(
                        e.FromJid,
                        e.Participant,
                        e.MessageId,
                        e.IsFromMe,
                        e.Timestamp,
                        placeholderType).ConfigureAwait(false);
                    return;
                }

                Contacts?.RegisterIncomingEnvelopeAliasHints(
                    e.SenderLid,
                    e.FromJid,
                    e.PeerRecipientPn,
                    e.PeerRecipientLid,
                    e.RecipientJid,
                    e.Participant,
                    e.ParticipantAlt);

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
                bool quietForSync =
                    (_initialSync != null && _initialSync.IsActive) ||
                    (_socket != null && _socket.IsAwaitingInitialSync);
                await Messages.RouteIncomingChatEnvelopeAsync(new IncomingRoutedEnvelopeRequest
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
                    SelfDisplayName = _authState?.Me?.Name,
                    QuietForSync = quietForSync,
                    IsWindowVisible = Unison.Uwp.App.IsWindowVisible,
                    IsActiveChat = IsActiveChatJid
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

        bool IOfflineReplayHost.IsActiveChat(string canonicalJid) => IsActiveChatJid(canonicalJid);

        bool IOfflineReplayHost.IsWindowVisible => Unison.Uwp.App.IsWindowVisible;

        void IOfflineReplayHost.NoteCatchUpProgress(string reason) => NoteFullHistoryCatchUpProgress(reason);

        void IOfflineReplayHost.SchedulePersist() => SchedulePersist();

        void IOfflineReplayHost.NotifyHistorySurfaceRefresh()
        {
            try
            {
                _messageStore?.ClearMemoryCache();
            }
            catch
            {
            }

            OnHistorySyncReceived?.Invoke(this, null);
        }

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
