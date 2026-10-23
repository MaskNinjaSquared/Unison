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

        private IReadOnlyList<string> GetAliasLinkedDirectChatJids(string chatJid)
        {
            return AliasLinkedDirectChats.Collect(
                ExpandNameLookupCandidates(chatJid),
                NormalizeJid,
                GetCanonicalJid);
        }

        private bool TryFindAliasLinkedMessage(string chatJid, string messageId, out string existingChatJid, out ChatMessage existingMessage)
        {
            return AliasLinkedMessageFind.TryFind(
                MessagesByChat,
                GetAliasLinkedDirectChatJids(chatJid),
                messageId,
                out existingChatJid,
                out existingMessage);
        }

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

                        // Update only affected rows; never scan all 300+ chat files.
                        foreach (var item in latestByChat)
                        {
                            var message = item.Value;
                            ChatPreviewKind kind = ChatPreviewNormalizer.InferKindFromMessage(message);
                            string preview = message?.Content;
                            if (string.IsNullOrWhiteSpace(preview) && kind != ChatPreviewKind.Revoked)
                            {
                                // The kind already knows what arrived. Deciding it again from
                                // IsImage alone labelled every video, sticker and voice note
                                // "[Message]".
                                preview = MediaPreviewTag.ForKind(kind) ?? "[Message]";
                            }

                            bool isGroupMsg = JidHelper.IsGroupJid(item.Key);
                            string stripParticipant = isGroupMsg
                                ? ParticipantStripIdentity.PreferForDisplay(
                                    message?.ParticipantJid,
                                    GetCanonicalJid(message?.ParticipantJid))
                                : null;
                            if (ChatFacade != null)
                            {
                                await ChatFacade.RefreshChatPreviewAsync(
                                    item.Key,
                                    preview,
                                    message?.Timestamp ?? DateTime.MinValue,
                                    message?.IsFromMe == true,
                                    kind,
                                    ChatPreviewNormalizer.FormatListAuthorPrefix(
                                        message,
                                        isGroupMsg,
                                        LocalizedChatStripLabels.SelfFallbackName(),
                                        stripParticipant),
                                    stripParticipant,
                                    isGroupMsg && message?.IsFromMe != true ? message?.SenderName : null);
                            }
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

            PlaceholderResendRequestState requestState = _missing.TakePeerResponse(response.StanzaId);

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

                var retryResponse = result.PlaceholderMessageResendResponse;
                if (retryResponse?.HasWebMessageInfoBytes == true && retryResponse.WebMessageInfoBytes != null)
                {
                    try
                    {
                        var webMessage = Proto.WebMessageInfo.Parser.ParseFrom(retryResponse.WebMessageInfoBytes);
                        await Task.Delay(500);
                        await UpsertRecoveredWebMessageInfoAsync(webMessage, requestState, "placeholder-resend-response");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Failed to decode placeholder resend response for stanza {response.StanzaId}: {ex.Message}");
                    }
                }
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
            if (node?.Attrs == null)
            {
                return;
            }

            node.Attrs.TryGetValue("class", out var ackClass);
            node.Attrs.TryGetValue("id", out var ackId);
            node.Attrs.TryGetValue("error", out var ackError);

            bool rejected;
            if (!PlaceholderResendAck.TryRead(ackClass, ackId, ackError, out rejected))
            {
                return;
            }

            PlaceholderResendRequestState requestState;
            if (!_missing.TryApplyAck(ackId, rejected, DateTime.UtcNow, out requestState))
            {
                return;
            }

            if (requestState == null)
            {
                return;
            }

            if (rejected)
            {
                Debug.WriteLine($"[WhatsAppService] placeholder resend ack rejected for {requestState.MessageId} in {requestState.ChatJid}: stanzaId={ackId}, error={ackError}");
            }
            else
            {
                Debug.WriteLine($"[WhatsAppService] placeholder resend ack accepted for {requestState.MessageId} in {requestState.ChatJid}: stanzaId={ackId}");
            }
        }

        private Task UpsertRecoveredWebMessageInfoAsync(Proto.WebMessageInfo webMessage, PlaceholderResendRequestState requestState, string source)
        {
            if (webMessage?.Message == null)
            {
                return Task.CompletedTask;
            }

            string remoteJid = webMessage.Key?.RemoteJid;
            if (string.IsNullOrWhiteSpace(remoteJid))
            {
                remoteJid = requestState?.ChatJid;
            }

            if (string.IsNullOrWhiteSpace(remoteJid) || string.IsNullOrWhiteSpace(webMessage.Key?.Id))
            {
                return Task.CompletedTask;
            }

            // Never call HandleDecryptedMessageAsync recursively here. This method is
            // reached from ProcessPeerDataOperationResponseAsync while the single
            // _messageIngestLock is already held. The old recursive await permanently
            // deadlocked the ingest pump as soon as a placeholder-resend response was
            // received; from that moment the socket still looked connected, but no
            // person or group message could update the UI.
            EnqueueDecryptedMessage(new DecryptedMessageEventArgs
            {
                FromJid = remoteJid,
                Participant = WebMessageParticipant.Resolve(webMessage),
                MessageId = webMessage.Key?.Id,
                Message = webMessage.Message,
                Timestamp = webMessage.MessageTimestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)webMessage.MessageTimestamp).UtcDateTime
                    : DateTime.MinValue,
                IsFromMe = webMessage.Key?.FromMe ?? false,
                PushName = webMessage.PushName,
                VerifiedName = null
            });

            Debug.WriteLine($"[WhatsAppService] Queued recovered message {webMessage.Key.Id} from {source}");
            return Task.CompletedTask;
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

        private void ObserveMediaHydration(
            ChatMessage chatMessage,
            MessageRenderInfo renderInfo,
            string messageId,
            string jid)
        {
            if (Messages == null)
            {
                return;
            }

            IncomingMediaHydrationPlan plan = IncomingMediaHydrationPlan.From(renderInfo);
            Messages.ScheduleIncomingMediaHydration(
                chatMessage,
                plan.HasSticker ? plan.Sticker : null,
                plan.HasImage ? plan.Image : null,
                messageId,
                jid);
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

        private void QueueMessageControlWork(string reason, Func<Task> work)
        {
            if (work == null)
            {
                return;
            }

            lock (_messageControlQueueLock)
            {
                Task previous = _messageControlQueueTail ?? Task.CompletedTask;
                _messageControlQueueTail = previous.ContinueWith(
                    async completedPrevious =>
                    {
                        // Observe a previous failure so the serial control queue is not
                        // torn down by one malformed App State or placeholder event.
                        if (completedPrevious.IsFaulted)
                        {
                            var ignored = completedPrevious.Exception;
                        }

                        try
                        {
                            await work();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[WhatsAppService] Background control message failed ({reason}): {ex.Message}");
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "messages",
                                "control-work-failed",
                                ex,
                                "reason=" + reason);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        }

        /// <summary>
        /// Status is not a chat: report the decoded row and skip ChatItem routing. Persistence is
        /// StatusFacade's, on <c>history_status</c>.
        /// </summary>
        private void IngestLiveStatus(Client.DecryptedMessageEventArgs e)
        {
            if (e?.Message == null)
            {
                return;
            }

            string author = IncomingLiveStatusAuthor.Resolve(
                NormalizeJid(e.Participant),
                e.IsFromMe,
                NormalizeJid(_authState?.Me?.Id));

            if (string.IsNullOrWhiteSpace(author))
            {
                Debug.WriteLine("[WhatsAppService] Live status skipped: no author id=" + e.MessageId);
                return;
            }

            HistoryStatus row = HistoryStatusBuilder.FromLive(
                author,
                e.MessageId,
                e.IsFromMe,
                e.PushName,
                e.Timestamp,
                e.Message);
            if (row == null)
            {
                string gaps = StatusItemDiagnostics.DescribeProtoGaps(e.Message);
                Debug.WriteLine(
                    "[WhatsAppService] Live status dropped id=" + e.MessageId +
                    "; author=" + author + "; " + gaps);
                RuntimeDiagnosticsService.Instance.Write(
                    "status",
                    "live-dropped",
                    "id=" + e.MessageId + "; author=" + author + "; " + gaps);
                return;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "status",
                "live-built",
                StatusItemDiagnostics.DescribeItem(row) + "; " +
                StatusItemDiagnostics.DescribeProtoGaps(e.Message));

            RaiseReport(
                () => OnLiveStatusReceived?.Invoke(this, row),
                nameof(OnLiveStatusReceived));
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
                    QueueMessageControlWork($"peer-response:{e.MessageId}", () => ProcessPeerDataOperationResponseAsync(response));
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
                    RegisterMissingMessage(e.FromJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, $"placeholder:{e.Message.PlaceholderMessage.Type}");
                    QueueMessageControlWork(
                        $"placeholder-resend:{e.MessageId}",
                        () => TryRequestPlaceholderResendAsync(e.FromJid, e.MessageId, "placeholder-message"));
                    return;
                }

                // Build PN/LID alias from message metadata immediately (works even when usync times out).
                foreach (IncomingAliasHint hint in IncomingEnvelopeAliasHints.Collect(
                    e.SenderLid,
                    e.FromJid,
                    e.PeerRecipientPn,
                    e.PeerRecipientLid,
                    e.RecipientJid,
                    e.Participant,
                    e.ParticipantAlt,
                    NormalizeJid))
                {
                    RegisterAliasMapping(hint.LidJid, hint.PnJid, hint.Source);
                }

                string normalizedFromJid = NormalizeJid(e.FromJid);
                if (IncomingEnvelopeDisposition.ClassifyAddress(normalizedFromJid) == IncomingEnvelopeKind.StatusBroadcast ||
                    IncomingEnvelopeDisposition.ClassifyAddress(e.FromJid) == IncomingEnvelopeKind.StatusBroadcast)
                {
                    IngestLiveStatus(e);
                    return;
                }

                bool isGroup = JidHelper.IsGroupJid(normalizedFromJid);

                // -- FAST PATH: offline replay duplicate detection --
                // When draining the offline batch (1000+ messages), skip the expensive
                // content extraction, alias resolution, and UI dispatches for messages
                // we already have on disk. Pushname capture from the raw 'notify' attr
                // is already handled independently in the OnMessage handler.
                // Enrichment still runs: a duplicate can carry a participant or a better
                // sender name the first delivery lacked, and the full path used to keep
                // those while this path threw them away.
                if (e.IsOffline && !string.IsNullOrEmpty(e.MessageId) && Messages != null)
                {
                    string fastJid = GetCanonicalJid(normalizedFromJid);
                    if (Messages.HasCachedMessageId(fastJid, e.MessageId, isGroup))
                    {
                        Messages.EnrichOfflineDuplicateFast(
                            fastJid,
                            e.MessageId,
                            e.IsFromMe,
                            e.Participant,
                            e.VerifiedName,
                            e.PushName);
                        ResolveMissingMessage(fastJid, e.MessageId, "offline-duplicate-fast");
                        return;
                    }
                    // Not a known duplicate ? fall through to full pipeline
                }

                string routingReason = isGroup ? "group-from" : null;
                string jid = isGroup ? GetCanonicalJid(normalizedFromJid) : ResolveLiveDirectChatJid(e, out routingReason);
                if (string.IsNullOrWhiteSpace(jid))
                {
                    jid = GetCanonicalJid(e.FromJid);
                    routingReason = routingReason ?? "fallback-from";
                }
                isGroup = JidHelper.IsGroupJid(jid);

                if (!isGroup)
                {
                    string normalizedRecipient = NormalizeJid(e.RecipientJid);
                    string normalizedPeerRecipientPn = NormalizeJid(e.PeerRecipientPn);
                    string normalizedPeerRecipientLid = NormalizeJid(e.PeerRecipientLid);
                    string normalizedSenderLid = NormalizeJid(e.SenderLid);
                    Debug.WriteLine(
                        $"[WhatsAppService] Direct live routing: id={e.MessageId}, from={normalizedFromJid} (self={IsSelfJid(normalizedFromJid)}), recipient={normalizedRecipient} (self={IsSelfJid(normalizedRecipient)}), peerRecipientPn={normalizedPeerRecipientPn} (self={IsSelfJid(normalizedPeerRecipientPn)}), peerRecipientLid={normalizedPeerRecipientLid} (self={IsSelfJid(normalizedPeerRecipientLid)}), senderLid={normalizedSenderLid} (self={IsSelfJid(normalizedSenderLid)}), isFromMe={e.IsFromMe}, finalChat={jid}, reason={routingReason}");

                    if (IncomingSelfChatCollapseGate.ShouldCollapse(
                            routingReason,
                            normalizedPeerRecipientLid,
                            jid))
                    {
                        if (ChatFacade != null)
                        {
                            QueueMessageControlWork(
                                "live-self-chat-collapse:" + e.MessageId,
                                () => ChatFacade.CollapseTransientDirectChatAsync(
                                    normalizedPeerRecipientLid,
                                    jid,
                                    "live-self-chat-collapse"));
                        }
                    }
                }

                IncomingEnvelopeKind chatControl = IncomingEnvelopeDisposition.ClassifyChatControl(e.Message);
                if (chatControl == IncomingEnvelopeKind.Revoke)
                {
                    QueueMessageControlWork(
                        "message-revoke:" + e.MessageId,
                        () => Messages != null
                            ? Messages.ApplyIncomingRevocationAsync(
                                jid, e.Message.ProtocolMessage.Key?.Id, e.MessageId)
                            : Task.CompletedTask);
                    return;
                }

                if (chatControl == IncomingEnvelopeKind.PinInChat)
                {
                    var pinMessage = e.Message.PinInChatMessage;
                    string pinTargetId = pinMessage?.Key?.Id;
                    bool pin = pinMessage != null &&
                        pinMessage.Type == Proto.Message.Types.PinInChatMessage.Types.Type.PinForAll;
                    uint duration = e.Message.MessageContextInfo?.MessageAddOnDurationInSecs ?? 0;
                    long senderTs = pinMessage?.SenderTimestampMs ?? 0;
                    QueueMessageControlWork(
                        "message-pin:" + e.MessageId,
                        () => Messages != null
                            ? Messages.ApplyIncomingPinInChatAsync(
                                jid,
                                pinTargetId,
                                pin,
                                senderTs,
                                duration)
                            : Task.CompletedTask);
                    return;
                }

                // Reactions: MessageFacade owns map/persist/notify; pump only builds context names.
                if (Messages != null)
                {
                    string reactionParticipant = NormalizeJid(e.Participant);
                    string reactionSenderName = e.IsFromMe
                        ? (_authState?.Me?.Name ?? LocalizedChatStripLabels.SelfFallbackName())
                        : (isGroup
                            ? ResolveDisplayName(
                                !string.IsNullOrEmpty(reactionParticipant) ? reactionParticipant : jid,
                                "sender")
                            : ResolveDisplayName(jid, "sender"));

                    var reactionContext = new ChatMessageMapContext
                    {
                        MessageId = e.MessageId,
                        ChatJid = jid,
                        RemoteJid = jid,
                        ParticipantJid = reactionParticipant,
                        SenderName = reactionSenderName,
                        IsFromMe = e.IsFromMe,
                        Timestamp = MessageTimestampValidity.KeepOrDiscard(e.Timestamp, DateTime.UtcNow)
                    };

                    if (await Messages.ApplyIncomingReactionAsync(e.Message, reactionContext)
                        .ConfigureAwait(false))
                    {
                        SetIncomingMessagePumpStage("reaction", e);
                        return;
                    }
                }

                SetIncomingMessagePumpStage("render", e);
                // Extract message render payload
                MessageRenderInfo renderInfo = MessageRenderReader.Read(
                    e.Message,
                    line => Log("[WhatsAppService] " + line));
                IncomingEmptyContentSkip emptySkip = IncomingEmptyContentSkip.For(
                    renderInfo?.Content,
                    e.Message?.SenderKeyDistributionMessage != null,
                    !string.IsNullOrEmpty(e.MessageId));
                if (emptySkip.ShouldSkip)
                {
                    // SenderKeyDistributionMessage-only payloads have no user-facing content
                    // They were already processed in SocketClient — just skip silently
                    if (emptySkip.Reason == IncomingEmptyContentReason.SenderKeyDistributionOnly)
                    {
                        Log("[WhatsAppService] SenderKeyDistribution-only message, no content to display");
                    }
                    else
                    {
                        Log("[WhatsAppService] No text content in message, skipping");
                    }

                    // A placeholder recovery that lands as an unrecognised type still has to
                    // clear the missing-message ledger, or the resend drain keeps asking for
                    // a message that will never draw.
                    if (emptySkip.ClearMissingLedger)
                    {
                        string skipJid = GetCanonicalJid(normalizedFromJid) ?? NormalizeJid(e.FromJid);
                        ResolveMissingMessage(skipJid, e.MessageId, "empty-content");
                    }

                    return;
                }

                string content = renderInfo.Content;


                // Update contact name cache if a pushName or verifiedName is provided
                string nameFromMsg = e.VerifiedName ?? e.PushName;
                if (!string.IsNullOrEmpty(nameFromMsg))
                {
                    Contacts?.AcceptIncomingPushName(
                        nameFromMsg,
                        e.IsFromMe,
                        e.Participant,
                        e.FromJid,
                        e.IsOffline);
                }

                // Resolve sender name and true 'IsFromMe' status.
                // Envelope participant is often a LID; participantAlt carries the phone. The bubble
                // walks aliases/roster later — the strip tip must prefer the phone up front or
                // ChatAuthorProjection never finds the name keyed under the PN.
                string participantJid = NormalizeJid(e.Participant);
                string participantAltJid = NormalizeJid(e.ParticipantAlt);
                string stripParticipantJid = ParticipantStripIdentity.PreferForDisplay(
                    participantJid,
                    participantAltJid);
                string resolvedParticipantName =
                    (!string.IsNullOrEmpty(stripParticipantJid)
                        ? ResolveDisplayName(stripParticipantJid, "sender")
                        : null)
                    ?? (!string.IsNullOrEmpty(participantJid)
                        ? ResolveDisplayName(participantJid, "sender")
                        : null);
                IncomingSenderResolution sender = IncomingSenderResolver.Resolve(
                    isGroup,
                    e.IsFromMe,
                    _authState?.Me?.Name,
                    LocalizedChatStripLabels.SelfFallbackName(),
                    resolvedParticipantName,
                    ResolveDisplayName(jid, "sender"),
                    !string.IsNullOrEmpty(participantJid) || !string.IsNullOrEmpty(stripParticipantJid));
                string senderName = sender.SenderName;
                bool isActuallyFromMe = sender.IsFromMe;
                
                // Decided once, here, so the badge and the toast cannot disagree about whether the
                // user is looking at this conversation. Initial sync / offline drain never pop
                // message toasts (unread still updates).
                bool quietForSync =
                    (_initialSync != null && _initialSync.IsActive) ||
                    (_socket != null && _socket.IsAwaitingInitialSync);
                IncomingAttention attention = IncomingAttention.For(
                    isActuallyFromMe,
                    IsActiveChatJid(jid),
                    Unison.Uwp.App.IsWindowVisible,
                    suppressToastForSync: quietForSync);

                // List preview body is unprefixed; group author is applied via LastMessageAuthor.
                // Strip identity prefers PN when known; FormatListAuthorPrefix falls back to the
                // phone digits rather than printing a LID.
                string displayContent = content;
                string listAuthorPrefix = isGroup
                    ? ChatPreviewNormalizer.FormatListAuthorPrefix(
                        new ChatMessage
                        {
                            SenderName = senderName,
                            IsFromMe = isActuallyFromMe,
                            ParticipantJid = participantJid
                        },
                        true,
                        LocalizedChatStripLabels.SelfFallbackName(),
                        stripParticipantJid)
                    : string.Empty;

                SetIncomingMessagePumpStage("model", e);
                // Domain ChatMessage via the MessageFacade (Kind resolved in mapper).
                // Naming the quoted author needs account / alias / directory — not in the envelope.
                QuotedContext quotedContext = QuotedContext.Read(e.Message);
                string quotedText = quotedContext.QuotedText;
                string quotedParticipantJid = quotedContext.QuotedParticipantJid;
                string quotedMessageId = quotedContext.QuotedMessageId;
                ChatPreviewKind quotedKind = quotedContext.QuotedKind;
                List<string> mentionedJids = quotedContext.MentionedJids;
                bool isForwarded = quotedContext.IsForwarded;
                bool mentionAll = quotedContext.MentionAll;
                string quotedSender = QuotedSenderNaming.Resolve(
                    quotedContext.QuotedParticipantJid,
                    p => IsSelfJid(p) || IsSelfLinkedJid(p),
                    p => ResolveDisplayName(p, "quote"),
                    p => ResolveDisplayName(p, "sender"),
                    LocalizedChatStripLabels.SelfFallbackName());

                if (Messages == null)
                {
                    Debug.WriteLine("[WhatsAppService] Dropping inbound message: MessageFacade is not attached.");
                    RuntimeDiagnosticsService.Instance.Write(
                        "messages",
                        "message-facade-missing",
                        "messageId=" + (e.MessageId ?? string.Empty));
                    return;
                }

                ChatMessage chatMessage = Messages.GetChatMessage(
                    new ChatMessageMapContext
                    {
                        MessageId = e.MessageId,
                        ChatJid = jid,
                        RemoteJid = jid,
                        ParticipantJid = participantJid,
                        SenderName = senderName,
                        IsFromMe = isActuallyFromMe,
                        Timestamp = MessageTimestampValidity.KeepOrDiscard(e.Timestamp, DateTime.UtcNow),
                        Status = isActuallyFromMe ? ApplyChatStatusPolicy(jid, ChatMessage.StatusSent) : null
                    },
                    IncomingChatMessageSnapshot.FromRender(
                        renderInfo,
                        content,
                        isForwarded,
                        quotedText,
                        quotedKind,
                        quotedSender,
                        quotedParticipantJid,
                        quotedMessageId,
                        mentionedJids,
                        mentionAll));

                IncomingMediaMetadata.Apply(chatMessage, renderInfo);

                Messages?.ApplyHeldMessageState(jid, chatMessage);

                ChatPreviewKind previewKind = LiveChatPreviewApplier.ResolveKind(chatMessage, renderInfo);

                SetIncomingMessagePumpStage("dedupe", e);
                IncomingTimelineAcceptResult timeline = Messages != null
                    ? Messages.AcceptIncomingTimeline(jid, chatMessage, isGroup)
                    : new IncomingTimelineAcceptResult
                    {
                        Kind = IncomingTimelineAcceptKind.Inserted,
                        Message = chatMessage
                    };

                if (timeline.Kind == IncomingTimelineAcceptKind.AliasConsolidated)
                {
                    Debug.WriteLine($"[WhatsAppService] Consolidated alias-linked duplicate {chatMessage.Id} from {timeline.AliasSourceChatJid} into {jid}");

                    string duplicateJidForPersist = timeline.AliasSourceChatJid;
                    ChatMessage consolidatedForPersist = timeline.ConsolidatedMessage;
                    QueueMessageControlWork(
                        "alias-duplicate-persist:" + chatMessage.Id,
                        () => Messages != null
                            ? Messages.FinalizeAliasConsolidateAsync(
                                jid,
                                duplicateJidForPersist,
                                chatMessage.Id,
                                consolidatedForPersist)
                            : Task.CompletedTask);

                    QueueOrApplyIncomingEnvelopePreview(
                        "alias-duplicate-preview:" + chatMessage.Id,
                        e.IsOffline,
                        jid,
                        displayContent,
                        chatMessage,
                        isGroup,
                        isActuallyFromMe,
                        countUnread: false,
                        markDirtyWhenOffline: true,
                        previewKind,
                        listAuthorPrefix,
                        stripParticipantJid,
                        senderName);
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Alias-linked duplicate message {e.MessageId} consolidated into {jid}");
                    }
                    return;
                }

                if (timeline.Kind == IncomingTimelineAcceptKind.DuplicateSameChat ||
                    timeline.Kind == IncomingTimelineAcceptKind.AliasLinkedDuplicate)
                {
                    if (timeline.ExistingChanged && timeline.Message != null)
                    {
                        if (Messages != null)
                        {
                            Messages.QueueIncomingPersist(jid, timeline.Message);
                        }
                        QueueChatMessagesChanged(jid);
                    }
                    if (timeline.Kind == IncomingTimelineAcceptKind.AliasLinkedDuplicate)
                    {
                        Debug.WriteLine($"[WhatsAppService] Alias-linked duplicate arrival detected for {chatMessage.Id}: existingChat={timeline.AliasSourceChatJid}, finalChat={jid}");
                    }
                    ResolveMissingMessage(jid, chatMessage.Id, "duplicate-arrival");
                    QueueOrApplyIncomingEnvelopePreview(
                        "duplicate-preview:" + chatMessage.Id,
                        e.IsOffline,
                        jid,
                        displayContent,
                        chatMessage,
                        isGroup,
                        isActuallyFromMe,
                        countUnread: false,
                        markDirtyWhenOffline: true,
                        previewKind,
                        listAuthorPrefix,
                        stripParticipantJid,
                        senderName);
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Duplicate message {e.MessageId} for {jid}, refreshed preview if needed");
                    }
                    return;
                }

                ResolveMissingMessage(jid, chatMessage.Id, "live-arrival");
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] Added message to chat {jid}. Total messages in memory: {MessagesByChat.CountFor(jid)}");
                }
                if (e.IsOffline)
                {
                    _ = ApplyIncomingEnvelopePreviewAsync(
                        jid,
                        displayContent,
                        chatMessage,
                        isOffline: true,
                        isGroup,
                        isActuallyFromMe,
                        countUnread: true,
                        markDirtyWhenOffline: false,
                        previewKind,
                        listAuthorPrefix,
                        stripParticipantJid,
                        senderName);
                    Messages?.QueueIncomingPersist(jid, chatMessage);

                    if (IsActiveChatJid(jid))
                    {
                        // The user may already be looking at the conversation while the
                        // reconnect replay is still draining. Refresh only that open chat.
                        QueueChatMessagesChanged(jid);
                    }
                    else
                    {
                        UnloadMessageCacheIfInactive(jid);
                    }

                    // Same hydration as live: stickers and images both download during replay.
                    ObserveMediaHydration(chatMessage, renderInfo, e.MessageId, jid);

                    return;
                }

                if (IsActiveChatJid(jid))
                {
                    QueueChatMessagesChanged(jid);
                }

                ObserveMediaHydration(chatMessage, renderInfo, e.MessageId, jid);

                // Update chat preview on UI thread via ChatFacade
                SetIncomingMessagePumpStage("ui-preview", e);
                string aliasLid = null;
                string aliasPn = null;
                if (JidAlias.TryGetValue(jid, out var aliasPair))
                {
                    aliasLid = JidHelper.IsLidJid(jid) ? jid : aliasPair;
                    aliasPn = JidHelper.IsPhoneJid(jid) ? jid : aliasPair;
                }

                if (ChatFacade == null)
                {
                    Debug.WriteLine("[WhatsAppService] Dropping live list apply: ChatFacade is not attached.");
                    RuntimeDiagnosticsService.Instance.Write(
                        "messages",
                        "chat-facade-missing",
                        "messageId=" + (e.MessageId ?? string.Empty));
                    return;
                }

                LiveIncomingChatListApplyResult listApply = await ChatFacade.ApplyLiveIncomingChatListAsync(
                    new LiveIncomingChatListApplyRequest
                    {
                        ChatJid = jid,
                        PreviewText = displayContent,
                        Timestamp = chatMessage.Timestamp,
                        PreviewKind = previewKind,
                        AuthorPrefix = listAuthorPrefix,
                        MentionedJids = chatMessage.MentionedJids,
                        IsFromMe = chatMessage.IsFromMe,
                        SendState = HistoryLiveMessageMapper.FromStatus(chatMessage.Status, chatMessage.IsFromMe),
                        MessageId = chatMessage.Id,
                        ParticipantJid = isGroup ? stripParticipantJid : null,
                        SenderName = isGroup && !isActuallyFromMe ? senderName : null,
                        UnreadDelta = attention.CountsAsUnread ? 1 : 0,
                        CountsAsUnread = attention.CountsAsUnread,
                        IsGroup = isGroup,
                        AliasLid = aliasLid,
                        AliasPn = aliasPn
                    }).ConfigureAwait(false);

                ChatItem notificationChat = listApply?.Chat;
                int totalUnreadForNotify = listApply != null ? listApply.TotalUnread : 0;

                SetIncomingMessagePumpStage("notify", e);
                string notificationName = notificationChat?.Name;
                if (string.IsNullOrWhiteSpace(notificationName))
                {
                    notificationName = !string.IsNullOrWhiteSpace(listApply?.DisplayName)
                        ? listApply.DisplayName
                        : ResolveDisplayName(jid, "notification");
                }

                Messages.NotifyLiveIncoming(
                    jid,
                    notificationName,
                    senderName,
                    content,
                    isGroup,
                    isActuallyFromMe,
                    attention.SuppressToast,
                    totalUnreadForNotify,
                    notificationChat);

                SetIncomingMessagePumpStage("persist-queue", e);
                // Persistencia em lote: evita reler, serializar e reescrever o JSON
                // inteiro para cada mensagem recebida.
                Messages?.QueueIncomingPersist(jid, chatMessage);
                UnloadMessageCacheIfInactive(jid);
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
        /// <summary>
        /// Offline tip recording stays on the pump thread (order with persist); live refresh
        /// stays on the control queue so strip work does not block ingestion.
        /// </summary>
        private void QueueOrApplyIncomingEnvelopePreview(
            string controlReason,
            bool isOffline,
            string jid,
            string displayContent,
            ChatMessage chatMessage,
            bool isGroup,
            bool isActuallyFromMe,
            bool countUnread,
            bool markDirtyWhenOffline,
            ChatPreviewKind previewKind,
            string listAuthorPrefix,
            string stripParticipantJid,
            string senderName)
        {
            if (isOffline)
            {
                _ = ApplyIncomingEnvelopePreviewAsync(
                    jid,
                    displayContent,
                    chatMessage,
                    isOffline: true,
                    isGroup,
                    isActuallyFromMe,
                    countUnread,
                    markDirtyWhenOffline,
                    previewKind,
                    listAuthorPrefix,
                    stripParticipantJid,
                    senderName);
                return;
            }

            QueueMessageControlWork(
                controlReason,
                () => ApplyIncomingEnvelopePreviewAsync(
                    jid,
                    displayContent,
                    chatMessage,
                    isOffline: false,
                    isGroup,
                    isActuallyFromMe,
                    countUnread,
                    markDirtyWhenOffline,
                    previewKind,
                    listAuthorPrefix,
                    stripParticipantJid,
                    senderName));
        }

        private Task ApplyIncomingEnvelopePreviewAsync(
            string jid,
            string displayContent,
            ChatMessage chatMessage,
            bool isOffline,
            bool isGroup,
            bool isActuallyFromMe,
            bool countUnread,
            bool markDirtyWhenOffline,
            ChatPreviewKind previewKind,
            string listAuthorPrefix,
            string stripParticipantJid,
            string senderName)
        {
            if (ChatFacade == null)
            {
                return Task.CompletedTask;
            }

            return ChatFacade.ApplyIncomingEnvelopePreviewAsync(new IncomingEnvelopePreviewRequest
            {
                Jid = jid,
                Preview = displayContent,
                Timestamp = chatMessage.Timestamp,
                IsGroup = isGroup,
                IsFromMe = isActuallyFromMe,
                IsOffline = isOffline,
                CountUnread = countUnread,
                MarkDirtyWhenOffline = markDirtyWhenOffline,
                Kind = previewKind,
                Status = chatMessage.Status,
                AuthorPrefix = listAuthorPrefix,
                ParticipantJid = isGroup ? stripParticipantJid : null,
                SenderName = isGroup && !isActuallyFromMe ? senderName : null
            });
        }

    }
}
