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
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
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

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private HashSet<string> GetOrBuildMessageIdIndex(string chatJid)
        {
            string normJid = NormalizeJid(chatJid);
            if (!_messageIdIndexByChat.TryGetValue(normJid, out var idSet))
            {
                if (MessagesByChat.TryGetValue(normJid, out var list))
                {
                    idSet = new HashSet<string>(
                        list.Where(m => m != null && !string.IsNullOrEmpty(m.Id)).Select(m => m.Id));
                }
                else
                {
                    idSet = new HashSet<string>();
                }

                _messageIdIndexByChat[normJid] = idSet;
            }

            return idSet;
        }

        private bool HasMessageId(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            return GetOrBuildMessageIdIndex(chatJid).Contains(messageId);
        }

        private IReadOnlyList<string> GetAliasLinkedDirectChatJids(string chatJid)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in ExpandNameLookupCandidates(chatJid))
            {
                string normalized = NormalizeJid(candidate);
                if (string.IsNullOrWhiteSpace(normalized) || normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidates.Add(normalized);
                candidates.Add(GetCanonicalJid(normalized));
            }

            return candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryFindAliasLinkedMessage(string chatJid, string messageId, out string existingChatJid, out ChatMessage existingMessage)
        {
            existingChatJid = null;
            existingMessage = null;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (var candidate in GetAliasLinkedDirectChatJids(chatJid))
            {
                if (!MessagesByChat.TryGetValue(candidate, out var messages) || messages == null)
                {
                    continue;
                }

                var match = messages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                if (match != null)
                {
                    existingChatJid = candidate;
                    existingMessage = match;
                    return true;
                }
            }

            return false;
        }

        private void RegisterMessageId(string chatJid, string messageId)
        {
            if (string.IsNullOrEmpty(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return;
            }

            GetOrBuildMessageIdIndex(chatJid).Add(messageId);
        }

        /// <summary>
        /// Checks whether a message ID exists in any alias-linked chat bucket.
        /// Used by the offline fast-path to avoid the heavier TryFindAliasLinkedMessage.
        /// </summary>
        private bool HasMessageIdInAnyAlias(string chatJid, string messageId)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            foreach (var candidate in GetAliasLinkedDirectChatJids(chatJid))
            {
                if (HasMessageId(candidate, messageId))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryConsolidateAliasDuplicateMessage(string targetChatJid, string sourceChatJid, string messageId, out ChatMessage consolidatedMessage)
        {
            consolidatedMessage = null;
            string normalizedTarget = NormalizeJid(targetChatJid);
            string normalizedSource = NormalizeJid(sourceChatJid);
            if (string.IsNullOrWhiteSpace(normalizedTarget) ||
                string.IsNullOrWhiteSpace(normalizedSource) ||
                string.IsNullOrWhiteSpace(messageId) ||
                string.Equals(normalizedTarget, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!MessagesByChat.TryGetValue(normalizedSource, out var sourceMessages) || sourceMessages == null)
            {
                return false;
            }

            var existingMessage = sourceMessages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
            if (existingMessage == null)
            {
                return false;
            }

            consolidatedMessage = existingMessage;
            if (!MessagesByChat.TryGetValue(normalizedTarget, out var targetMessages) || targetMessages == null)
            {
                targetMessages = new List<ChatMessage>();
                MessagesByChat[normalizedTarget] = targetMessages;
            }

            if (!HasMessageId(normalizedTarget, messageId))
            {
                ChatMessageOrder.InsertSorted(targetMessages, existingMessage);
                RegisterMessageId(normalizedTarget, messageId);
            }

            sourceMessages.Remove(existingMessage);
            if (_messageIdIndexByChat.TryGetValue(normalizedSource, out var sourceIndex))
            {
                sourceIndex.Remove(messageId);
            }

            return true;
        }

        private void RegisterMissingMessage(string chatJid, string participant, string messageId, bool isFromMe, DateTime timestamp, string reason)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            lock (_missingMessageLock)
            {
                if (!_pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId))
                {
                    byMessageId = new Dictionary<string, MissingMessageCandidate>(StringComparer.Ordinal);
                    _pendingMissingMessagesByChat[normJid] = byMessageId;
                }

                if (!byMessageId.TryGetValue(messageId, out var candidate))
                {
                    candidate = new MissingMessageCandidate
                    {
                        ChatJid = normJid,
                        MessageId = messageId,
                        FirstSeenUtc = DateTime.UtcNow
                    };
                    byMessageId[messageId] = candidate;
                }

                candidate.Participant = participant;
                candidate.IsFromMe = isFromMe;
                candidate.MessageTimestamp = timestamp;
                candidate.Reason = reason;
                candidate.LastSeenUtc = DateTime.UtcNow;
            }

            if (!ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine($"[WhatsAppService] Queued missing-message recovery for {messageId} in {normJid} (reason={reason})");
            }
        }

        private void ResolveMissingMessage(string chatJid, string messageId, string source)
        {
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            CancellationTokenSource scheduledCts = null;
            string pendingRequestId = null;

            lock (_missingMessageLock)
            {
                if (_pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId))
                {
                    if (byMessageId.TryGetValue(messageId, out var candidate))
                    {
                        scheduledCts = candidate.PlaceholderScheduleCts;
                        pendingRequestId = candidate.LastPlaceholderRequestId;
                    }

                    byMessageId.Remove(messageId);
                    if (byMessageId.Count == 0)
                    {
                        _pendingMissingMessagesByChat.Remove(normJid);
                    }
                }

                if (!string.IsNullOrWhiteSpace(pendingRequestId))
                {
                    _placeholderResendRequestsByStanzaId.Remove(pendingRequestId);
                }
            }

            if (scheduledCts != null)
            {
                try
                {
                    scheduledCts.Cancel();
                    scheduledCts.Dispose();
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

        private bool TryGetMissingMessage(string chatJid, string messageId, out MissingMessageCandidate candidate)
        {
            candidate = null;
            string normJid = NormalizeJid(chatJid);
            if (string.IsNullOrWhiteSpace(normJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            lock (_missingMessageLock)
            {
                return _pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId) &&
                       byMessageId.TryGetValue(messageId, out candidate);
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

            DateTime utcNow = DateTime.UtcNow;
            CancellationTokenSource scheduleCts = null;
            lock (_missingMessageLock)
            {
                if (candidate.PlaceholderRequestCount >= 2 ||
                    candidate.PlaceholderRequestInFlight ||
                    candidate.PlaceholderScheduleCts != null)
                {
                    return Task.FromResult(false);
                }

                if (candidate.LastPlaceholderRequestUtc != DateTime.MinValue &&
                    utcNow - candidate.LastPlaceholderRequestUtc < PlaceholderResendResponseTimeout)
                {
                    return Task.FromResult(false);
                }

                scheduleCts = new CancellationTokenSource();
                candidate.PlaceholderScheduleCts = scheduleCts;
                candidate.PlaceholderScheduledForUtc = utcNow.Add(PlaceholderResendDispatchDelay);
            }

            Debug.WriteLine($"[WhatsAppService] placeholder resend scheduled for {candidate.MessageId} in {candidate.ChatJid} (trigger={trigger}, dueInMs={(int)PlaceholderResendDispatchDelay.TotalMilliseconds})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PlaceholderResendDispatchDelay, scheduleCts.Token);
                }
                catch (TaskCanceledException)
                {
                    Debug.WriteLine($"[WhatsAppService] placeholder resend cancelled before send for {messageId} in {chatJid} (trigger={trigger})");
                    return;
                }

                MissingMessageCandidate currentCandidate;
                lock (_missingMessageLock)
                {
                    if (!TryGetMissingMessage(chatJid, messageId, out currentCandidate) ||
                        currentCandidate.PlaceholderScheduleCts != scheduleCts)
                    {
                        return;
                    }

                    currentCandidate.PlaceholderScheduleCts = null;
                    currentCandidate.PlaceholderScheduledForUtc = DateTime.MinValue;
                    currentCandidate.PlaceholderRequestInFlight = true;
                }

                string stanzaId = null;
                try
                {
                    var key = new Proto.MessageKey
                    {
                        RemoteJid = currentCandidate.ChatJid,
                        Id = currentCandidate.MessageId,
                        FromMe = currentCandidate.IsFromMe,
                        Participant = currentCandidate.Participant ?? string.Empty
                    };

                    stanzaId = _socket.GenerateMessageId();
                    lock (_missingMessageLock)
                    {
                        if (!TryGetMissingMessage(chatJid, messageId, out currentCandidate))
                        {
                            return;
                        }

                        currentCandidate.LastPlaceholderRequestUtc = DateTime.UtcNow;
                        currentCandidate.PlaceholderRequestCount++;
                        currentCandidate.LastPlaceholderRequestId = stanzaId;
                        _placeholderResendRequestsByStanzaId[stanzaId] = new PlaceholderResendRequestState
                        {
                            ChatJid = currentCandidate.ChatJid,
                            MessageId = currentCandidate.MessageId,
                            RequestedAtUtc = DateTime.UtcNow,
                            Trigger = trigger
                        };
                    }

                    string sentStanzaId = await _socket.RequestPlaceholderResendAsync(key, stanzaId);
                    if (!string.Equals(sentStanzaId, stanzaId, StringComparison.Ordinal))
                    {
                        Debug.WriteLine($"[WhatsAppService] PLACEHOLDER_MESSAGE_RESEND stanza id changed unexpectedly: tracked={stanzaId}, sent={sentStanzaId}");
                    }

                    Debug.WriteLine($"[WhatsAppService] placeholder resend sent for {messageId} in {chatJid} (trigger={trigger}, stanzaId={stanzaId})");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(PlaceholderResendResponseTimeout);

                        PlaceholderResendRequestState timedOutState = null;
                        lock (_missingMessageLock)
                        {
                            if (_placeholderResendRequestsByStanzaId.TryGetValue(stanzaId, out timedOutState))
                            {
                                _placeholderResendRequestsByStanzaId.Remove(stanzaId);
                                if (TryGetMissingMessage(timedOutState.ChatJid, timedOutState.MessageId, out var timedOutCandidate) &&
                                    string.Equals(timedOutCandidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                                {
                                    timedOutCandidate.PlaceholderRequestInFlight = false;
                                }
                            }
                        }

                        if (timedOutState != null)
                        {
                            string timeoutKind = timedOutState.AckAccepted ? $"accepted/no-payload, ackAt={timedOutState.AckAcceptedUtc:O}" : "no-ack";
                            Debug.WriteLine($"[WhatsAppService] placeholder resend timed out for {timedOutState.MessageId} in {timedOutState.ChatJid} (stanzaId={stanzaId}, {timeoutKind})");
                        }
                    });
                }
                catch (Exception ex)
                {
                    lock (_missingMessageLock)
                    {
                        if (!string.IsNullOrWhiteSpace(stanzaId))
                        {
                            _placeholderResendRequestsByStanzaId.Remove(stanzaId);
                        }
                        if (TryGetMissingMessage(chatJid, messageId, out currentCandidate))
                        {
                            currentCandidate.PlaceholderRequestInFlight = false;
                            if (string.Equals(currentCandidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                            {
                                currentCandidate.LastPlaceholderRequestId = null;
                                currentCandidate.PlaceholderRequestCount = Math.Max(0, currentCandidate.PlaceholderRequestCount - 1);
                            }
                        }
                    }

                    Debug.WriteLine($"[WhatsAppService] PLACEHOLDER_MESSAGE_RESEND send failed for {messageId} in {chatJid}: {ex.Message}");
                }
                finally
                {
                    scheduleCts.Dispose();
                }
            });

            return Task.FromResult(true);
        }

        private bool ShouldDeferPlaceholderResend(string trigger, out string reason)
        {
            reason = null;

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return false;
            }

            if (ShouldDeferReconnectReplayWork())
            {
                reason = "reconnect-replay-active";
                return true;
            }

            if (_socket.IsAwaitingInitialSync)
            {
                reason = "awaiting-initial-sync";
                return true;
            }

                if (_historyBackfillActive)
                {
                    reason = "history-backfill-active";
                    return true;
                }

            lock (_historyOnDemandLock)
            {
                if (_historyOnDemandInFlight.Count > 0)
                {
                    reason = "history-on-demand-in-flight";
                    return true;
                }
            }

            return false;
        }

        private static bool IsPeerOrSelfMissingMessage(MissingMessageCandidate candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            if (candidate.IsFromMe)
            {
                return true;
            }

            string chatJid = candidate.ChatJid ?? string.Empty;
            return chatJid.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) ||
                   chatJid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeMissingMessageCandidate(MissingMessageCandidate candidate)
        {
            if (candidate == null)
            {
                return "<null>";
            }

            return $"{candidate.MessageId}@{candidate.ChatJid}:fromMe={candidate.IsFromMe},requests={candidate.PlaceholderRequestCount},ts={candidate.MessageTimestamp:O},reason={candidate.Reason}";
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
            lock (_missingMessageLock)
            {
                pending = _pendingMissingMessagesByChat
                    .Values
                    .SelectMany(byMessageId => byMessageId.Values)
                    .Where(candidate =>
                        candidate != null &&
                        !candidate.PlaceholderRequestInFlight &&
                        candidate.PlaceholderScheduleCts == null &&
                        candidate.PlaceholderRequestCount < 2)
                    .Select(candidate => new MissingMessageCandidate
                    {
                        ChatJid = candidate.ChatJid,
                        Participant = candidate.Participant,
                        MessageId = candidate.MessageId,
                        IsFromMe = candidate.IsFromMe,
                        MessageTimestamp = candidate.MessageTimestamp,
                        Reason = candidate.Reason,
                        FirstSeenUtc = candidate.FirstSeenUtc,
                        LastSeenUtc = candidate.LastSeenUtc,
                        PlaceholderRequestCount = candidate.PlaceholderRequestCount
                    })
                    .OrderBy(candidate => candidate.PlaceholderRequestCount)
                    .ThenByDescending(IsPeerOrSelfMissingMessage)
                    .ThenByDescending(candidate => candidate.MessageTimestamp)
                    .ThenByDescending(candidate => candidate.LastSeenUtc)
                    .ToList();

                totalEligible = pending.Count;
                pending = pending
                    .Take(maxRequests)
                    .ToList();
            }

            if (pending.Count == 0)
            {
                Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain found no pending messages ({trigger})");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Deferred placeholder resend drain selected {pending.Count}/{totalEligible} eligible message(s) ({trigger}): {string.Join(" | ", pending.Select(DescribeMissingMessageCandidate))}");

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
            lock (_missingMessageLock)
            {
                if (_activeChatReconcileCooldownByChat.TryGetValue(normJid, out var cooldownUntil) &&
                    cooldownUntil > utcNow)
                {
                    return false;
                }
                _activeChatReconcileCooldownByChat[normJid] = utcNow.Add(ActiveChatReconcileCooldown);
            }

            List<string> pendingIds;
            lock (_missingMessageLock)
            {
                pendingIds = _pendingMissingMessagesByChat.TryGetValue(normJid, out var byMessageId)
                    ? byMessageId.Keys.Take(maxRequests).ToList()
                    : new List<string>();
            }

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
                        .OrderBy(message => message.Timestamp)
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
                            string preview = message?.Content;
                            if (string.IsNullOrWhiteSpace(preview))
                            {
                                preview = message?.IsImage == true ? "[Image]" : "[Message]";
                            }

                            await RefreshChatPreviewFromReplayAsync(
                                item.Key,
                                preview,
                                message?.Timestamp ?? DateTime.MinValue,
                                item.Key.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase),
                                message?.IsFromMe == true,
                                ChatPreviewNormalizer.InferKindFromMessage(message));
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

        private sealed class MessageRenderInfo
        {
            public string Content { get; set; }
            public bool IsImage { get; set; }
            public bool IsVideo { get; set; }
            public bool IsSticker { get; set; }
            public string Caption { get; set; }
            public Proto.Message.Types.ImageMessage ImageMessage { get; set; }
            public Proto.Message.Types.StickerMessage StickerMessage { get; set; }
            public Proto.Message.Types.VideoMessage VideoMessage { get; set; }
            public bool IsAudio { get; set; }
            public bool IsVoice { get; set; }
            public bool IsDocument { get; set; }
            public Proto.Message.Types.DocumentMessage DocumentMessage { get; set; }
            public Proto.Message.Types.AudioMessage AudioMessage { get; set; }
            public string QuotedText { get; set; }
            public string QuotedSenderName { get; set; }
            public System.Collections.Generic.List<string> MentionedJids { get; set; }

            public ChatPreviewKind PreviewKind
            {
                get
                {
                    if (IsSticker) return ChatPreviewKind.Sticker;
                    if (IsImage) return ChatPreviewKind.Image;
                    if (IsVideo) return ChatPreviewKind.Video;
                    if (IsDocument) return ChatPreviewKind.Document;
                    if (IsVoice || IsAudio) return ChatPreviewKind.Voice;
                    return ChatPreviewKind.Text;
                }
            }
        }

        private Proto.Message UnwrapMessage(Proto.Message msg)
        {
            return HistorySyncContentFilter.Unwrap(msg);
        }

        private static Proto.ContextInfo GetContextInfo(Proto.Message unwrapped)
        {
            if (unwrapped == null)
            {
                return null;
            }

            return unwrapped.ExtendedTextMessage?.ContextInfo
                ?? unwrapped.ImageMessage?.ContextInfo
                ?? unwrapped.VideoMessage?.ContextInfo
                ?? unwrapped.AudioMessage?.ContextInfo
                ?? unwrapped.DocumentMessage?.ContextInfo
                ?? unwrapped.StickerMessage?.ContextInfo
                ?? unwrapped.ButtonsMessage?.ContextInfo
                ?? unwrapped.ButtonsResponseMessage?.ContextInfo
                ?? unwrapped.TemplateButtonReplyMessage?.ContextInfo
                ?? unwrapped.ListMessage?.ContextInfo
                ?? unwrapped.ListResponseMessage?.ContextInfo
                ?? unwrapped.InteractiveMessage?.ContextInfo
                ?? unwrapped.ContactMessage?.ContextInfo
                ?? unwrapped.LocationMessage?.ContextInfo
                ?? unwrapped.LiveLocationMessage?.ContextInfo;
        }

        private void ApplyContextInfoExtras(
            Proto.Message msg,
            out string quotedText,
            out string quotedSender,
            out string quotedParticipantJid,
            out string quotedMessageId,
            out ChatPreviewKind quotedKind,
            out List<string> mentionedJids,
            out bool isForwarded)
        {
            quotedText = null;
            quotedSender = null;
            quotedParticipantJid = null;
            quotedMessageId = null;
            quotedKind = ChatPreviewKind.Text;
            mentionedJids = null;
            isForwarded = false;

            Proto.Message unwrapped = UnwrapMessage(msg);
            isForwarded = HistorySyncContentFilter.ReadIsForwarded(unwrapped);
            Proto.ContextInfo ctx = GetContextInfo(unwrapped);
            if (ctx == null)
            {
                return;
            }

            if (ctx.MentionedJid != null && ctx.MentionedJid.Count > 0)
            {
                mentionedJids = new List<string>();
                for (int i = 0; i < ctx.MentionedJid.Count; i++)
                {
                    string norm = NormalizeJid(ctx.MentionedJid[i]);
                    if (!string.IsNullOrEmpty(norm) && !mentionedJids.Contains(norm))
                    {
                        mentionedJids.Add(norm);
                    }
                }

                if (mentionedJids.Count == 0)
                {
                    mentionedJids = null;
                }
            }

            if (ctx.QuotedMessage == null)
            {
                return;
            }

            if (ctx.HasStanzaId && !string.IsNullOrWhiteSpace(ctx.StanzaId))
            {
                quotedMessageId = ctx.StanzaId;
            }

            MessageRenderInfo quotedInfo = ExtractMessageRenderInfo(ctx.QuotedMessage);
            if (quotedInfo != null)
            {
                quotedKind = quotedInfo.PreviewKind;
                string raw = quotedInfo.Content ?? string.Empty;
                ChatPreviewKind? hint = quotedKind == ChatPreviewKind.Text
                    ? null
                    : (ChatPreviewKind?)quotedKind;
                ChatPreviewNormalizer.Normalize(raw, hint, out _, out quotedText);
                if (string.IsNullOrWhiteSpace(quotedText) &&
                    !string.IsNullOrWhiteSpace(quotedInfo.Caption))
                {
                    quotedText = quotedInfo.Caption;
                }

                // Media quotes with no caption: keep QuotedText empty â€” the bubble strip
                // shows icon + localized label from QuotedKind (not legacy [Image] tags).
            }

            string participant = NormalizeJid(ctx.Participant);
            if (!string.IsNullOrEmpty(participant))
            {
                quotedParticipantJid = participant;
                if (IsSelfJid(participant) || IsSelfLinkedJid(participant))
                {
                    quotedSender = SelfListDisplayName();
                }
                else
                {
                    quotedSender = ResolveDisplayName(participant, "quote");
                    if (string.IsNullOrWhiteSpace(quotedSender) ||
                        quotedSender.IndexOf('@') >= 0)
                    {
                        quotedSender = GetResolvedName(participant);
                    }
                }
            }
        }

        private static bool IsValidMessageTimestamp(DateTime timestamp)
        {
            return timestamp != DateTime.MinValue &&
                   timestamp.Year >= 2009 &&
                   timestamp <= DateTime.UtcNow.AddDays(2);
        }

        private static DateTime NormalizeIncomingTimestamp(DateTime timestamp, bool isOffline)
        {
            if (IsValidMessageTimestamp(timestamp)) return timestamp;
            // Never turn a replayed server event without a timestamp into a new message.
            // Outgoing bubbles stamp DateTime.UtcNow before entering this path.
            return DateTime.MinValue;
        }

        private static byte[] DecodeBase64Safe(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return Convert.FromBase64String(value); }
            catch { return null; }
        }

        private async Task HandleMessageRevocationAsync(string chatJid, Proto.Message.Types.ProtocolMessage protocol, string envelopeMessageId = null)
        {
            string targetId = protocol?.Key?.Id;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(targetId)) return;

            string canonical = GetCanonicalJid(chatJid);

            // Older builds stored the revoke envelope itself as a fresh
            // "[Message Deleted]" item. Remove that synthetic row when the same
            // event is replayed; the real target below keeps its original timestamp.
            if (!string.IsNullOrWhiteSpace(envelopeMessageId) &&
                !string.Equals(envelopeMessageId, targetId, StringComparison.Ordinal))
            {
                foreach (var pair in MessagesByChat.ToList())
                {
                    if (!string.Equals(GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                    var synthetic = pair.Value?.FirstOrDefault(m => string.Equals(m?.Id, envelopeMessageId, StringComparison.Ordinal));
                    if (synthetic != null) pair.Value.Remove(synthetic);
                }
                if (_messageStore != null)
                {
                    try { await _messageStore.DeleteMessageAsync(canonical, envelopeMessageId); } catch { }
                }
            }

            ChatMessage target = null;
            foreach (var pair in MessagesByChat.ToList())
            {
                if (!string.Equals(GetCanonicalJid(pair.Key), canonical, StringComparison.OrdinalIgnoreCase)) continue;
                target = pair.Value?.FirstOrDefault(m => string.Equals(m?.Id, targetId, StringComparison.Ordinal));
                if (target != null) break;
            }

            // If the chat is not resident, read the retained local window once. A revoke is
            // an update to an existing message; it must never be inserted as a new "current" item.
            if (target == null)
            {
                ChatMessage persisted = null;
                try
                {
                    HistoryMessage row = await _historyMessages.GetAsync(canonical, targetId).ConfigureAwait(false);
                    persisted = HistoryMessageMapper.ToChatMessage(row);
                }
                catch
                {
                }

                target = persisted;
            }

            if (target == null) return;
            target.Content = "[Message Deleted]";
            target.Caption = string.Empty;
            target.Kind = ChatMessageKind.Text;
            target.IsImage = false;
            target.ImageUri = null;
            target.ImageUrl = null;
            target.ImageDirectPath = null;
            target.ImageMediaKeyBase64 = null;
            target.ImageFileEncSha256Base64 = null;
            target.ImageMimeType = null;
            target.VideoUri = null;
            target.VideoPosterUri = null;
            target.VideoUrl = null;
            target.VideoDirectPath = null;
            target.VideoMediaKeyBase64 = null;
            target.VideoFileEncSha256Base64 = null;
            target.VideoMimeType = null;
            target.VideoDurationSeconds = 0;
            target.IsAudio = false;
            target.AudioUri = null;
            target.AudioUrl = null;
            target.AudioDirectPath = null;
            target.AudioMediaKeyBase64 = null;
            target.AudioFileEncSha256Base64 = null;
            await SaveMessageAsync(canonical, target);

            if (IsActiveChatJid(canonical)) QueueChatMessagesChanged(canonical);
            var latest = MessagesByChat.ContainsKey(canonical)
                ? MessagesByChat[canonical].Where(m => m != null).OrderBy(m => m.Timestamp).LastOrDefault()
                : null;
            if (latest != null && string.Equals(latest.Id, target.Id, StringComparison.Ordinal))
            {
                await RefreshChatPreviewFromReplayAsync(
                    canonical,
                    target.Content,
                    target.Timestamp,
                    canonical.EndsWith("@g.us"),
                    target.IsFromMe,
                    ChatPreviewNormalizer.InferKindFromMessage(target));
            }
        }

        private MessageRenderInfo ExtractMessageRenderInfo(Proto.Message msg)
        {
            var unwrapped = UnwrapMessage(msg);
            if (unwrapped == null) return null;

            // Simple text message (Conversation)
            if (!string.IsNullOrEmpty(unwrapped.Conversation))
            {
                return new MessageRenderInfo { Content = unwrapped.Conversation };
            }

            // Extended text message
            if (unwrapped.ExtendedTextMessage != null && !string.IsNullOrEmpty(unwrapped.ExtendedTextMessage.Text))
            {
                return new MessageRenderInfo { Content = unwrapped.ExtendedTextMessage.Text };
            }

            // Sticker before image: live MergeFrom can leave both fields; ImageMessage is often a thumbnail.
            if (unwrapped.StickerMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = "[Sticker]",
                    IsSticker = true,
                    StickerMessage = unwrapped.StickerMessage
                };
            }

            // Image message (caption optional)
            if (unwrapped.ImageMessage != null)
            {
                string caption = unwrapped.ImageMessage.Caption ?? "";
                string preview = string.IsNullOrWhiteSpace(caption) ? "[Image]" : $"[Image] {caption}";
                return new MessageRenderInfo
                {
                    Content = preview,
                    IsImage = true,
                    Caption = caption,
                    ImageMessage = unwrapped.ImageMessage
                };
            }

            // Video message with caption
            if (unwrapped.VideoMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = !string.IsNullOrEmpty(unwrapped.VideoMessage.Caption)
                        ? $"[Video] {unwrapped.VideoMessage.Caption}"
                        : "[Video]",
                    IsVideo = true,
                    Caption = unwrapped.VideoMessage.Caption ?? "",
                    VideoMessage = unwrapped.VideoMessage
                };
            }

            // Document message
            if (unwrapped.DocumentMessage != null)
            {
                return new MessageRenderInfo
                {
                    Content = !string.IsNullOrEmpty(unwrapped.DocumentMessage.FileName)
                        ? $"[Document] {unwrapped.DocumentMessage.FileName}"
                        : "[Document]",
                    IsDocument = true,
                    DocumentMessage = unwrapped.DocumentMessage
                };
            }

            // Audio/Voice message
            if (unwrapped.AudioMessage != null)
            {
                bool isVoice = unwrapped.AudioMessage.Ptt == true;
                return new MessageRenderInfo
                {
                    Content = isVoice ? "[Voice Message]" : "[Audio]",
                    IsAudio = true,
                    IsVoice = isVoice,
                    AudioMessage = unwrapped.AudioMessage
                };
            }

            // Reaction envelopes are handled by IChatMessageMapper / IReactionMapper (not timeline rows).
            if (unwrapped.ReactionMessage != null)
            {
                return null;
            }

            // Poll creation
            if (unwrapped.PollCreationMessage != null)
            {
                return new MessageRenderInfo { Content = $"[Poll] {unwrapped.PollCreationMessage.Name}" };
            }

            // Protocol message (e.g. delete)
            if (unwrapped.ProtocolMessage != null)
            {
                if ((int)unwrapped.ProtocolMessage.Type == 0)
                    return null; // handled as an update to the original message
                if (unwrapped.ProtocolMessage.HistorySyncNotification != null)
                    return null;
                if (unwrapped.ProtocolMessage.PeerDataOperationRequestResponseMessage != null)
                {
                    var resp = unwrapped.ProtocolMessage.PeerDataOperationRequestResponseMessage;
                    var result = resp.PeerDataOperationResult?.FirstOrDefault();
                    string fullCode = result?.FullHistorySyncOnDemandRequestResponse?.ResponseCode.ToString() ?? "";
                    string chunkCode = result?.HistorySyncChunkRetryResponse?.ResponseCode.ToString() ?? "";
                    Log($"[WhatsAppService] PeerDataOperationResponse message observed: type={resp.PeerDataOperationRequestType}, stanzaId={resp.StanzaId}, fullHistoryCode={fullCode}, chunkRetryCode={chunkCode}");
                    return null;
                }
            }

            if (unwrapped.ContactMessage != null) return new MessageRenderInfo { Content = $"[Contact] {unwrapped.ContactMessage.DisplayName}" };
            if (unwrapped.LocationMessage != null) return new MessageRenderInfo { Content = "[Location]" };

            // Call logs
            if (unwrapped.CallLogMesssage != null)
            {
                string outcome = unwrapped.CallLogMesssage.CallOutcome.ToString();
                string duration = unwrapped.CallLogMesssage.DurationSecs > 0 ? $" ({unwrapped.CallLogMesssage.DurationSecs}s)" : "";
                return new MessageRenderInfo { Content = $"[Call] {outcome}{duration}" };
            }
            if (unwrapped.ScheduledCallCreationMessage != null)
            {
                return new MessageRenderInfo { Content = $"[Scheduled Call] {unwrapped.ScheduledCallCreationMessage.Title}" };
            }
            if (unwrapped.Call != null)
            {
                return new MessageRenderInfo { Content = "[Call]" };
            }

            Debug.WriteLine($"[WhatsAppService] Unknown message type (Proto Msg IDs: {string.Join(", ", unwrapped.GetType().GetProperties().Where(p => p.PropertyType == typeof(object) || p.PropertyType.GetTypeInfo().IsClass).Where(p => p.GetValue(unwrapped) != null).Select(p => p.Name))}), no content extracted");
            return null;
        }

        /// <summary>
        /// Extracts user-visible preview text from a Proto.Message.
        /// </summary>
        private string ExtractMessageContent(Proto.Message msg)
        {
            return ExtractMessageRenderInfo(msg)?.Content;
        }

        private async Task ProcessPeerDataOperationResponseAsync(Proto.Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            if (response == null)
            {
                return;
            }

            Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse received: stanzaId={response.StanzaId}, requestType={response.PeerDataOperationRequestType}, resultCount={response.PeerDataOperationResult?.Count ?? 0}");

            PlaceholderResendRequestState requestState = null;
            lock (_missingMessageLock)
            {
                if (!string.IsNullOrWhiteSpace(response.StanzaId))
                {
                    _placeholderResendRequestsByStanzaId.TryGetValue(response.StanzaId, out requestState);
                    _placeholderResendRequestsByStanzaId.Remove(response.StanzaId);
                    if (requestState != null &&
                        TryGetMissingMessage(requestState.ChatJid, requestState.MessageId, out var candidate) &&
                        string.Equals(candidate.LastPlaceholderRequestId, response.StanzaId, StringComparison.Ordinal))
                    {
                        candidate.PlaceholderRequestInFlight = false;
                    }
                }
            }

            HistoryOnDemandRequestState historyRequestState = null;
            lock (_historyOnDemandLock)
            {
                if (!string.IsNullOrWhiteSpace(response.StanzaId))
                {
                    _historyOnDemandRequestById.TryGetValue(response.StanzaId, out historyRequestState);
                }
            }

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
                Debug.WriteLine($"[WhatsAppService] PeerDataOperationResponse completed without immediate history payload: requestType={historyRequestState.RequestType}, stanzaId={response.StanzaId}, chat={historyRequestState.ChatJid ?? "<full-history>"}, baseline={historyRequestState.BaselineMessageCount}, trigger={historyRequestState.TriggerReason ?? "unspecified"}");
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

            if (!string.Equals(ackClass, "message", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(ackId))
            {
                return;
            }

            PlaceholderResendRequestState requestState = null;
            bool rejected = !string.IsNullOrWhiteSpace(ackError);

            lock (_missingMessageLock)
            {
                if (!_placeholderResendRequestsByStanzaId.TryGetValue(ackId, out requestState))
                {
                    return;
                }

                if (rejected)
                {
                    _placeholderResendRequestsByStanzaId.Remove(ackId);
                    if (TryGetMissingMessage(requestState.ChatJid, requestState.MessageId, out var candidate) &&
                        string.Equals(candidate.LastPlaceholderRequestId, ackId, StringComparison.Ordinal))
                    {
                        candidate.PlaceholderRequestInFlight = false;
                    }
                }
                else
                {
                    requestState.AckAccepted = true;
                    requestState.AckAcceptedUtc = DateTime.UtcNow;
                }
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
                Participant = ResolveHistoryParticipantJid(webMessage),
                MessageId = webMessage.Key?.Id,
                Message = webMessage.Message,
                Timestamp = webMessage.MessageTimestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds((long)webMessage.MessageTimestamp).LocalDateTime
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

            lock (_incomingMessageQueueLock)
            {
                if (message.IsOffline)
                {
                    _offlineIncomingMessageQueue.Enqueue(message);
                }
                else
                {
                    _liveIncomingMessageQueue.Enqueue(message);
                }
            }

            RestartIncomingMessagePumpIfNeeded();
        }

        private void RestartIncomingMessagePumpIfNeeded()
        {
            int generation;
            lock (_incomingMessageQueueLock)
            {
                if (_incomingMessagePumpRunning ||
                    (_liveIncomingMessageQueue.Count == 0 && _offlineIncomingMessageQueue.Count == 0))
                {
                    return;
                }

                _incomingMessagePumpRunning = true;
                generation = _incomingMessagePumpGeneration;
                _incomingMessagePumpStage = "starting";
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                _incomingMessagePumpTask = Task.Run(() => ProcessIncomingMessageQueueAsync(generation));
            }

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-start",
                "generation=" + generation);
        }

        private void SetIncomingMessagePumpStage(string stage, Client.DecryptedMessageEventArgs message = null)
        {
            lock (_incomingMessageQueueLock)
            {
                _incomingMessagePumpStage = string.IsNullOrWhiteSpace(stage) ? "unknown" : stage;
                _incomingMessagePumpCurrent = message ?? _incomingMessagePumpCurrent;
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
            }
        }

        private void ResetIncomingMessagePump(string reason, bool requeueCurrent)
        {
            int generation;
            int liveDepth;
            int offlineDepth;
            lock (_incomingMessageQueueLock)
            {
                var current = _incomingMessagePumpCurrent;
                _incomingMessagePumpGeneration++;
                generation = _incomingMessagePumpGeneration;

                if (requeueCurrent && current != null)
                {
                    if (current.IsOffline)
                    {
                        _offlineIncomingMessageQueue.Enqueue(current);
                    }
                    else
                    {
                        _liveIncomingMessageQueue.Enqueue(current);
                    }
                }

                _incomingMessagePumpCurrent = null;
                _incomingMessagePumpRunning = false;
                _incomingMessagePumpTask = Task.CompletedTask;
                _incomingMessagePumpStage = "reset:" + reason;
                _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                liveDepth = _liveIncomingMessageQueue.Count;
                offlineDepth = _offlineIncomingMessageQueue.Count;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "messages",
                "incoming-pump-reset",
                "reason=" + reason + "; generation=" + generation +
                "; requeued=" + requeueCurrent + "; qLive=" + liveDepth + "; qOffline=" + offlineDepth);
        }

        private bool IsIncomingMessagePumpStalled(TimeSpan limit)
        {
            lock (_incomingMessageQueueLock)
            {
                if (!_incomingMessagePumpRunning || _incomingMessagePumpStageUtcTicks <= 0)
                {
                    return false;
                }

                bool hasWork = _incomingMessagePumpCurrent != null ||
                               _liveIncomingMessageQueue.Count > 0 ||
                               _offlineIncomingMessageQueue.Count > 0;
                if (!hasWork)
                {
                    return false;
                }

                var stageUtc = new DateTime(_incomingMessagePumpStageUtcTicks, DateTimeKind.Utc);
                return DateTime.UtcNow - stageUtc > limit;
            }
        }

        private async Task ProcessIncomingMessageQueueAsync(int generation)
        {
            while (true)
            {
                Client.DecryptedMessageEventArgs next = null;
                lock (_incomingMessageQueueLock)
                {
                    if (generation != _incomingMessagePumpGeneration)
                    {
                        return;
                    }

                    // Always service real-time traffic first. Offline replay records are
                    // timestamp-guarded, so processing them later cannot overwrite a
                    // newer preview.
                    if (_liveIncomingMessageQueue.Count > 0)
                    {
                        next = _liveIncomingMessageQueue.Dequeue();
                    }
                    else if (_offlineIncomingMessageQueue.Count > 0)
                    {
                        next = _offlineIncomingMessageQueue.Dequeue();
                    }
                    else
                    {
                        _incomingMessagePumpCurrent = null;
                        _incomingMessagePumpRunning = false;
                        _incomingMessagePumpStage = "idle";
                        _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                        return;
                    }

                    _incomingMessagePumpCurrent = next;
                    _incomingMessagePumpStage = "handle";
                    _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
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
                                "; stage=" + _incomingMessagePumpStage +
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

                            bool requeueTimedOutMessage = true;
                            lock (_incomingMessageQueueLock)
                            {
                                if (!string.IsNullOrWhiteSpace(next.MessageId))
                                {
                                    // Requeue once. If the same message blocks again,
                                    // skip that one item so the rest of the conversation
                                    // and all other chats can continue updating.
                                    requeueTimedOutMessage = _incomingMessageTimeoutIds.Add(next.MessageId);
                                }
                            }

                            ResetIncomingMessagePump(
                                requeueTimedOutMessage ? "message-timeout-retry" : "message-timeout-skip",
                                requeueCurrent: requeueTimedOutMessage);
                            RestartIncomingMessagePumpIfNeeded();
                            return;
                        }
                    }

                    await handleTask;
                    if (!string.IsNullOrWhiteSpace(next.MessageId))
                    {
                        lock (_incomingMessageQueueLock)
                        {
                            _incomingMessageTimeoutIds.Remove(next.MessageId);
                        }
                    }
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
                        "; stage=" + _incomingMessagePumpStage);
                }
                finally
                {
                    lock (_incomingMessageQueueLock)
                    {
                        if (generation == _incomingMessagePumpGeneration &&
                            ReferenceEquals(_incomingMessagePumpCurrent, next))
                        {
                            _incomingMessagePumpCurrent = null;
                            _incomingMessagePumpStage = "next";
                            _incomingMessagePumpStageUtcTicks = DateTime.UtcNow.Ticks;
                        }
                    }
                }
            }
        }

        private async Task WaitForIncomingMessageQueueDrainAsync(int timeoutMs)
        {
            Task pump;
            lock (_incomingMessageQueueLock)
            {
                pump = _incomingMessagePumpTask ?? Task.CompletedTask;
            }

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
        /// Status is not a chat: persist on <c>history_status</c> and skip ChatItem routing.
        /// </summary>
        private async Task IngestLiveStatusAsync(Client.DecryptedMessageEventArgs e)
        {
            if (_statusService == null || e?.Message == null)
            {
                return;
            }

            string author = NormalizeJid(e.Participant);
            if (string.IsNullOrWhiteSpace(author) && e.IsFromMe)
            {
                author = NormalizeJid(_authState?.Me?.Id);
            }

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
                return;
            }

            try
            {
                await _statusService.TryIngestLiveAsync(row).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Live status ingest failed: " + ex.Message);
            }
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

                if (e.Message?.ProtocolMessage?.PeerDataOperationRequestResponseMessage != null)
                {
                    var response = e.Message.ProtocolMessage.PeerDataOperationRequestResponseMessage;
                    QueueMessageControlWork($"peer-response:{e.MessageId}", () => ProcessPeerDataOperationResponseAsync(response));
                    return;
                }

                // Both of these are the session's business now: the app state module inside
                // Unison.Socket takes the key share and recovers from a fatal sync itself.
                if (e.Message?.ProtocolMessage?.AppStateFatalExceptionNotification != null ||
                    e.Message?.ProtocolMessage?.AppStateSyncKeyShare != null)
                {
                    return;
                }

                if (e.Message?.PlaceholderMessage != null)
                {
                    RegisterMissingMessage(e.FromJid, e.Participant, e.MessageId, e.IsFromMe, e.Timestamp, $"placeholder:{e.Message.PlaceholderMessage.Type}");
                    QueueMessageControlWork(
                        $"placeholder-resend:{e.MessageId}",
                        () => TryRequestPlaceholderResendAsync(e.FromJid, e.MessageId, "placeholder-message"));
                    return;
                }

                // Build PN/LID alias from message metadata immediately (works even when usync times out).
                if (!string.IsNullOrEmpty(e.SenderLid) && !string.IsNullOrEmpty(e.FromJid) && e.FromJid.EndsWith("@s.whatsapp.net"))
                {
                    RegisterAliasMapping(e.SenderLid, e.FromJid, "sender_lid");
                }
                if (!string.IsNullOrEmpty(e.PeerRecipientPn) && !string.IsNullOrEmpty(e.FromJid) && e.FromJid.EndsWith("@lid"))
                {
                    RegisterAliasMapping(e.FromJid, e.PeerRecipientPn, "peer_recipient_pn");
                }
                if (!string.IsNullOrEmpty(e.PeerRecipientLid) && !string.IsNullOrEmpty(e.RecipientJid) && e.RecipientJid.EndsWith("@s.whatsapp.net"))
                {
                    RegisterAliasMapping(e.PeerRecipientLid, e.RecipientJid, "peer_recipient_lid");
                }
                if (!string.IsNullOrEmpty(e.Participant) && !string.IsNullOrEmpty(e.ParticipantAlt))
                {
                    string participant = NormalizeJid(e.Participant);
                    string alternate = NormalizeJid(e.ParticipantAlt);
                    if (participant.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                        RegisterAliasMapping(participant, alternate, "group-participant-alt");
                    else if (alternate.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                        RegisterAliasMapping(alternate, participant, "group-participant-alt");
                }

                string normalizedFromJid = NormalizeJid(e.FromJid);
                if (JidHelper.IsStatusBroadcast(normalizedFromJid) ||
                    JidHelper.IsStatusBroadcast(e.FromJid))
                {
                    await IngestLiveStatusAsync(e).ConfigureAwait(false);
                    return;
                }

                bool isGroup = normalizedFromJid.EndsWith("@g.us");

                // -- FAST PATH: offline replay duplicate detection --
                // When draining the offline batch (1000+ messages), skip the expensive
                // content extraction, alias resolution, and UI dispatches for messages
                // we already have on disk. Pushname capture from the raw 'notify' attr
                // is already handled independently in the OnMessage handler.
                if (e.IsOffline && !string.IsNullOrEmpty(e.MessageId))
                {
                    if (isGroup)
                    {
                        string fastGroupJid = GetCanonicalJid(normalizedFromJid);
                        if (HasMessageId(fastGroupJid, e.MessageId))
                        {
                            ResolveMissingMessage(fastGroupJid, e.MessageId, "offline-duplicate-fast");
                            return;
                        }
                    }
                    else
                    {
                        // For DMs, check the from JID and all known alias buckets
                        string fastDmJid = GetCanonicalJid(normalizedFromJid);
                        if (HasMessageId(fastDmJid, e.MessageId) ||
                            HasMessageIdInAnyAlias(normalizedFromJid, e.MessageId))
                        {
                            ResolveMissingMessage(fastDmJid, e.MessageId, "offline-duplicate-fast");
                            return;
                        }
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
                isGroup = jid.EndsWith("@g.us");

                if (!isGroup)
                {
                    string normalizedRecipient = NormalizeJid(e.RecipientJid);
                    string normalizedPeerRecipientPn = NormalizeJid(e.PeerRecipientPn);
                    string normalizedPeerRecipientLid = NormalizeJid(e.PeerRecipientLid);
                    string normalizedSenderLid = NormalizeJid(e.SenderLid);
                    Debug.WriteLine(
                        $"[WhatsAppService] Direct live routing: id={e.MessageId}, from={normalizedFromJid} (self={IsSelfJid(normalizedFromJid)}), recipient={normalizedRecipient} (self={IsSelfJid(normalizedRecipient)}), peerRecipientPn={normalizedPeerRecipientPn} (self={IsSelfJid(normalizedPeerRecipientPn)}), peerRecipientLid={normalizedPeerRecipientLid} (self={IsSelfJid(normalizedPeerRecipientLid)}), senderLid={normalizedSenderLid} (self={IsSelfJid(normalizedSenderLid)}), isFromMe={e.IsFromMe}, finalChat={jid}, reason={routingReason}");

                    if (string.Equals(routingReason, "self-chat", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(normalizedPeerRecipientLid) &&
                        !string.Equals(normalizedPeerRecipientLid, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        QueueMessageControlWork(
                            "live-self-chat-collapse:" + e.MessageId,
                            () => MergeTransientDirectChatIntoCanonicalAsync(
                                normalizedPeerRecipientLid,
                                jid,
                                "live-self-chat-collapse"));
                    }
                }

                if (e.Message?.ProtocolMessage != null && (int)e.Message.ProtocolMessage.Type == 0)
                {
                    QueueMessageControlWork(
                        "message-revoke:" + e.MessageId,
                        () => HandleMessageRevocationAsync(jid, e.Message.ProtocolMessage, e.MessageId));
                    return;
                }

                if (e.Message?.PinInChatMessage != null)
                {
                    uint duration = e.Message.MessageContextInfo?.MessageAddOnDurationInSecs ?? 0;
                    QueueMessageControlWork(
                        "message-pin:" + e.MessageId,
                        () => HandlePinInChatMessageAsync(jid, e.Message.PinInChatMessage, duration));
                    return;
                }

                // Reactions: MessageFacade maps onto parent; WA only persists / notifies.
                if (_messageService != null)
                {
                    string reactionParticipant = NormalizeJid(e.Participant);
                    string reactionSenderName = e.IsFromMe
                        ? (_authState?.Me?.Name ?? SelfListDisplayName())
                        : (isGroup
                            ? GetResolvedName(!string.IsNullOrEmpty(reactionParticipant) ? reactionParticipant : jid)
                            : GetResolvedName(jid));

                    if (!MessagesByChat.ContainsKey(jid))
                    {
                        MessagesByChat[jid] = new List<ChatMessage>();
                    }

                    var reactionContext = new ChatMessageMapContext
                    {
                        MessageId = e.MessageId,
                        ChatJid = jid,
                        RemoteJid = jid,
                        ParticipantJid = reactionParticipant,
                        SenderName = reactionSenderName,
                        IsFromMe = e.IsFromMe,
                        Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline)
                    };

                    ChatMessage reactionParent;
                    if (_messageService.TryHandleReaction(e.Message, reactionContext, MessagesByChat[jid], out reactionParent))
                    {
                        SetIncomingMessagePumpStage("reaction", e);
                        if (reactionParent != null)
                        {
                            await SaveMessageAsync(jid, reactionParent).ConfigureAwait(false);
                            if (IsActiveChatJid(jid))
                            {
                                QueueChatMessagesChanged(jid);
                            }
                        }
                        else
                        {
                            Log($"[WhatsAppService] Reaction target not found yet: chat={jid}, id={e.MessageId}");
                        }
                        return;
                    }
                }

                SetIncomingMessagePumpStage("render", e);
                // Extract message render payload
                var renderInfo = ExtractMessageRenderInfo(e.Message);
                string content = renderInfo?.Content;
                if (string.IsNullOrEmpty(content))
                {
                    // SenderKeyDistributionMessage-only payloads have no user-facing content
                    // They were already processed in SocketClient ? just skip silently
                    if (e.Message?.SenderKeyDistributionMessage != null)
                    {
                        Log("[WhatsAppService] SenderKeyDistribution-only message, no content to display");
                    }
                    else
                    {
                        Log("[WhatsAppService] No text content in message, skipping");
                    }
                    return;
                }


                // Update contact name cache if a pushName or verifiedName is provided
                string nameFromMsg = e.VerifiedName ?? e.PushName;
                if (!string.IsNullOrEmpty(nameFromMsg))
                {
                    // The push name on a message we sent is our own, whoever the message went to.
                    // Attributing it to the conversation instead - which is what happens when the
                    // sender is read as "participant or chat" - writes the user's name over their
                    // contact's, and leaves the user themselves nameless.
                    string senderJid = e.IsFromMe
                        ? NormalizeJid(_authState?.Me?.Id)
                        : NormalizeJid(e.Participant ?? e.FromJid);
                    if (e.IsFromMe)
                    {
                        CaptureSelfPushName(nameFromMsg, "message-echo");
                    }

                    if (string.IsNullOrEmpty(senderJid))
                    {
                        senderJid = NormalizeJid(e.Participant ?? e.FromJid);
                    }

                    // Update if we don't have a name, or if the current name is just the JID/number
                    if (!ContactNames.TryGetValue(senderJid, out var existingName) || existingName.Contains("@") || existingName == senderJid.Split('@')[0])
                    {
                        string sanitized = SanitizeContactLabel(nameFromMsg, senderJid);
                        if (string.IsNullOrEmpty(sanitized))
                        {
                            if (IsSelfJid(senderJid))
                            {
                                Log($"[WhatsAppService] Explicit 'You' label observed for SELF JID {senderJid}. Ignoring and keeping numeric identity.");
                            }
                            else
                            {
                                Log($"[WhatsAppService] Ignoring PushName 'You' for NON-SELF JID {senderJid} (spoof prevention).");
                            }
                            Log($"[WhatsAppService] Ignoring PushName 'You' for {senderJid} to prevent spoofing");
                        }
                        else
                        {
                            ContactNames[senderJid] = sanitized;
                            RememberPersonName(senderJid, sanitized);
                            if (!e.IsOffline)
                            {
                                Log($"[WhatsAppService] Updated contact name for {senderJid} from message metadata: {sanitized}");
                            }
                        }
                    }
                }

                // Resolve sender name and true 'IsFromMe' status:
                
                string senderName;
                bool isActuallyFromMe = e.IsFromMe;

                if (isGroup)
                {
                    if (e.IsFromMe)
                    {
                        senderName = _authState?.Me?.Name ?? SelfListDisplayName();
                    }
                    else if (!string.IsNullOrEmpty(e.Participant))
                    {
                        string participantJid = NormalizeJid(e.Participant);
                        senderName = GetResolvedName(participantJid);
                    }
                    else
                    {
                        senderName = GetResolvedName(jid);
                    }
                }
                else
                {
                    // 1-on-1 Chat
                    if (e.IsFromMe)
                    {
                        // If it's from me, it could be a message I sent from this device (Local)
                        // OR a message I sent from my phone (Synced).
                        // In Unison, we want to know if 'I' am the author or if the 'Other Person' is.
                        senderName = _authState?.Me?.Name ?? SelfListDisplayName();
                        isActuallyFromMe = true;
                    }
                    else
                    {
                        // Message from the other person
                        senderName = GetResolvedName(jid);
                        isActuallyFromMe = false;
                    }
                }
                
                // List preview body is unprefixed; group author is applied via LastMessageAuthor.
                string displayContent = content;
                string listAuthorPrefix = isGroup
                    ? ChatPreviewNormalizer.FormatListAuthorPrefix(
                        new ChatMessage { SenderName = senderName, IsFromMe = isActuallyFromMe },
                        true,
                        SelfListDisplayName())
                    : string.Empty;

                SetIncomingMessagePumpStage("model", e);
                // Domain ChatMessage via the MessageFacade (Kind resolved in mapper).
                ChatMessage chatMessage;
                ApplyContextInfoExtras(e.Message, out string quotedText, out string quotedSender, out string quotedParticipantJid, out string quotedMessageId, out var quotedKind, out var mentionedJids, out bool isForwarded);

                if (_messageService != null)
                {
                    chatMessage = _messageService.GetChatMessage(
                        new ChatMessageMapContext
                        {
                            MessageId = e.MessageId,
                            ChatJid = jid,
                            RemoteJid = jid,
                            ParticipantJid = NormalizeJid(e.Participant),
                            SenderName = senderName,
                            IsFromMe = isActuallyFromMe,
                            Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline),
                            Status = isActuallyFromMe ? ApplyChatStatusPolicy(jid, ChatMessage.StatusSent) : null
                        },
                        new ChatMessageContentSnapshot
                        {
                            Text = content,
                            IsImage = renderInfo?.IsImage == true,
                            IsVideo = renderInfo?.IsVideo == true,
                            IsSticker = renderInfo?.IsSticker == true,
                            IsAudio = renderInfo?.IsAudio == true,
                            IsVoice = renderInfo?.IsVoice == true,
                            IsDocument = renderInfo?.IsDocument == true,
                            Caption = renderInfo?.Caption ?? "",
                            IsForwarded = isForwarded,
                            QuotedText = quotedText,
                            QuotedKind = quotedKind,
                            QuotedSenderName = quotedSender,
                            QuotedParticipantJid = quotedParticipantJid,
                            QuotedMessageId = quotedMessageId,
                            MentionedJids = mentionedJids
                        });
                }
                else
                {
                    // Temporary escape hatch until MessageFacade is always attached.
                    chatMessage = new ChatMessage
                    {
                        Id = e.MessageId,
                        Content = content,
                        Kind = ChatPreviewNormalizer.ResolveKind(
                            renderInfo?.IsImage == true,
                            renderInfo?.IsVideo == true,
                            renderInfo?.IsSticker == true,
                            renderInfo?.IsAudio == true,
                            renderInfo?.IsVoice == true,
                            renderInfo?.IsDocument == true),
                        Caption = renderInfo?.Caption ?? "",
                        IsForwarded = isForwarded,
                        Timestamp = NormalizeIncomingTimestamp(e.Timestamp, e.IsOffline),
                        IsFromMe = isActuallyFromMe,
                        SenderName = senderName,
                        RemoteJid = jid,
                        ParticipantJid = NormalizeJid(e.Participant),
                        Status = isActuallyFromMe ? ApplyChatStatusPolicy(jid, ChatMessage.StatusSent) : null,
                        QuotedText = quotedText,
                        QuotedKind = quotedKind,
                        QuotedSenderName = quotedSender,
                        QuotedParticipantJid = quotedParticipantJid,
                        QuotedMessageId = quotedMessageId,
                        MentionedJids = mentionedJids
                    };
                }

                if (renderInfo?.IsAudio == true && renderInfo.AudioMessage != null)
                {
                    ApplyAudioMetadata(chatMessage, renderInfo.AudioMessage);
                }

                if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                {
                    ApplyImageMetadata(chatMessage, renderInfo.ImageMessage);
                }

                if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                {
                    ApplyStickerMetadata(chatMessage, renderInfo.StickerMessage);
                }

                if (renderInfo?.IsVideo == true && renderInfo.VideoMessage != null)
                {
                    ApplyVideoMetadata(chatMessage, renderInfo.VideoMessage);
                }

                if (renderInfo?.IsDocument == true && renderInfo.DocumentMessage != null)
                {
                    ApplyDocumentMetadata(chatMessage, renderInfo.DocumentMessage);
                }

                ApplyPendingStateToMessage(jid, chatMessage);

                ChatPreviewKind previewKind = ResolvePreviewKind(chatMessage, renderInfo);

                SetIncomingMessagePumpStage("dedupe", e);
                // Add to MessagesByChat
                if (!MessagesByChat.ContainsKey(jid))
                {
                    MessagesByChat[jid] = new List<ChatMessage>();
                }

                string duplicateChatJid = null;
                ChatMessage duplicateMessage = null;
                bool hasAliasLinkedDuplicate = !isGroup &&
                    !string.IsNullOrEmpty(chatMessage.Id) &&
                    TryFindAliasLinkedMessage(jid, chatMessage.Id, out duplicateChatJid, out duplicateMessage);

                ChatMessage consolidatedMessage;
                if (!string.IsNullOrEmpty(chatMessage.Id) &&
                    hasAliasLinkedDuplicate &&
                    !string.Equals(NormalizeJid(duplicateChatJid), jid, StringComparison.OrdinalIgnoreCase) &&
                    TryConsolidateAliasDuplicateMessage(jid, duplicateChatJid, chatMessage.Id, out consolidatedMessage))
                {
                    Debug.WriteLine($"[WhatsAppService] Consolidated alias-linked duplicate {chatMessage.Id} from {duplicateChatJid} into {jid}");

                    string duplicateJidForPersist = NormalizeJid(duplicateChatJid);
                    ChatMessage consolidatedForPersist = consolidatedMessage;
                    QueueMessageControlWork(
                        "alias-duplicate-persist:" + chatMessage.Id,
                        async () =>
                        {
                            await _messageStore.DeleteMessageAsync(duplicateJidForPersist, chatMessage.Id);
                            if (consolidatedForPersist != null)
                            {
                                await SaveMessageAsync(jid, consolidatedForPersist);
                            }
                            await DeduplicateChatsAsync("live-direct-alias-duplicate");
                        });

                    if (!e.IsOffline)
                    {
                        QueueMessageControlWork(
                            "alias-duplicate-preview:" + chatMessage.Id,
                            () => RefreshChatPreviewFromReplayAsync(
                                jid,
                                displayContent,
                                chatMessage.Timestamp,
                                isGroup,
                                isActuallyFromMe,
                                previewKind));
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
                        RecordOfflineReplayChatSummary(
                            jid,
                            displayContent,
                            chatMessage.Timestamp,
                            isGroup,
                            isActuallyFromMe,
                            countUnread: false,
                            previewKind);
                    }
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Alias-linked duplicate message {e.MessageId} consolidated into {jid}");
                    }
                    return;
                }

                // Fallback duplicate guard for empty IDs / index drift.
                if ((!string.IsNullOrEmpty(chatMessage.Id) && HasMessageId(jid, chatMessage.Id)) ||
                    (!string.IsNullOrEmpty(chatMessage.Id) && MessagesByChat[jid].Any(m => m.Id == chatMessage.Id)) ||
                    hasAliasLinkedDuplicate)
                {
                    var existingMessage = MessagesByChat[jid].FirstOrDefault(m => string.Equals(m?.Id, chatMessage.Id, StringComparison.Ordinal));
                    bool existingChanged = false;
                    if (existingMessage != null)
                    {
                        if (chatMessage.IsFromMe && ShouldApplyMessageStatus(existingMessage.Status, chatMessage.Status))
                        {
                            existingMessage.Status = chatMessage.Status;
                            existingChanged = true;
                        }
                        if (string.IsNullOrWhiteSpace(existingMessage.ParticipantJid) &&
                            !string.IsNullOrWhiteSpace(chatMessage.ParticipantJid))
                        {
                            existingMessage.ParticipantJid = chatMessage.ParticipantJid;
                            existingChanged = true;
                        }
                        if (IsWeakHistorySenderName(existingMessage.SenderName) &&
                            !IsWeakHistorySenderName(chatMessage.SenderName))
                        {
                            existingMessage.SenderName = chatMessage.SenderName;
                            existingChanged = true;
                        }
                        if (existingChanged)
                        {
                            QueueOfflineReplayMessageForPersist(jid, existingMessage);
                            SchedulePersist();
                            QueueChatMessagesChanged(jid);
                        }
                    }
                    if (hasAliasLinkedDuplicate)
                    {
                        Debug.WriteLine($"[WhatsAppService] Alias-linked duplicate arrival detected for {chatMessage.Id}: existingChat={duplicateChatJid}, finalChat={jid}");
                    }
                    ResolveMissingMessage(jid, chatMessage.Id, "duplicate-arrival");
                    if (!e.IsOffline)
                    {
                        QueueMessageControlWork(
                            "duplicate-preview:" + chatMessage.Id,
                            () => RefreshChatPreviewFromReplayAsync(
                                jid,
                                displayContent,
                                chatMessage.Timestamp,
                                isGroup,
                                isActuallyFromMe,
                                previewKind));
                    }
                    else
                    {
                        MarkOfflineReplayChatDirty(jid);
                        RecordOfflineReplayChatSummary(
                            jid,
                            displayContent,
                            chatMessage.Timestamp,
                            isGroup,
                            isActuallyFromMe,
                            countUnread: false,
                            previewKind);
                    }
                    if (!e.IsOffline)
                    {
                        Log($"[WhatsAppService] Duplicate message {e.MessageId} for {jid}, refreshed preview if needed");
                    }
                    return;
                }

                ChatMessageOrder.InsertSorted(MessagesByChat[jid], chatMessage);
                TrimInMemoryMessageWindow(jid);
                RegisterMessageId(jid, chatMessage.Id);
                ResolveMissingMessage(jid, chatMessage.Id, "live-arrival");
                if (!e.IsOffline)
                {
                    Log($"[WhatsAppService] Added message to chat {jid}. Total messages in memory: {MessagesByChat[jid].Count}");
                }

                if (e.IsOffline)
                {
                    RecordOfflineReplayChatSummary(
                        jid,
                        displayContent,
                        chatMessage.Timestamp,
                        isGroup,
                        isActuallyFromMe,
                        countUnread: true,
                        previewKind);
                    QueueOfflineReplayMessageForPersist(jid, chatMessage);

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

                    // Stickers still need media hydration during offline replay.
                    if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                    {
                        _ = HydrateStickerForMessageAsync(chatMessage, renderInfo.StickerMessage, e.MessageId, jid);
                    }
                    else if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null && IsActiveChatJid(jid))
                    {
                        _ = HydrateImageForMessageAsync(chatMessage, renderInfo.ImageMessage, e.MessageId, jid);
                    }

                    return;
                }

                if (IsActiveChatJid(jid))
                {
                    QueueChatMessagesChanged(jid);
                }

                if (renderInfo?.IsImage == true && renderInfo.ImageMessage != null)
                {
                    _ = HydrateImageForMessageAsync(chatMessage, renderInfo.ImageMessage, e.MessageId, jid);
                }

                if (renderInfo?.IsSticker == true && renderInfo.StickerMessage != null)
                {
                    _ = HydrateStickerForMessageAsync(chatMessage, renderInfo.StickerMessage, e.MessageId, jid);
                }

                // Update chat preview on UI thread
                SetIncomingMessagePumpStage("ui-preview", e);
                ChatItem notificationChat = null;
                await RunOnUiThreadAsync(() =>
                    {
                        string canonicalLookup = GetCanonicalJid(jid) ?? jid;
                        var chat = Chats.FirstOrDefault(c =>
                            string.Equals(
                                GetCanonicalJid(c.JID),
                                canonicalLookup,
                                StringComparison.OrdinalIgnoreCase));
                        
                        // Create new chat entry if this JID isn't known yet
                        if (chat == null)
                        {
                            string chatName = ResolveDisplayName(jid, "chat");
                            chat = new ChatItem
                            {
                                JID = GetCanonicalJid(jid),
                                Name = chatName,
                                Kind = ResolveChatKind(jid),
                                UnreadCount = 0
                            };
                            Chats.Insert(0, chat);
                            Log($"[WhatsAppService] Created new chat entry for {jid} ({chatName})");
                            _ = DeduplicateChatsAsync("incoming-new-chat");

                            // If this JID is a PN that has a mapped LID, or vice-versa, trigger a merge scan
                            if (JidAlias.TryGetValue(jid, out var alias))
                            {
                                string lid = jid.EndsWith("@lid") ? jid : alias;
                                string pn = jid.EndsWith("@s.whatsapp.net") ? jid : alias;
                                _ = CheckAndMergeDuplicateChatsAsync(lid, pn);
                            }

                            // If name is still naked, trigger resolution
                            string bare = chat.JID.Split('@')[0];
                            if (chat.Name == bare || chat.Name.Contains("@"))
                            {
                                _ = ResolveMissingNamesAsync();
                            }
                        }
                        
                        // Atualiza todas as linhas PN/LID equivalentes. Uma linha duplicada
                        // podia continuar visivel com mensagem antiga mesmo apos o envio.
                        ApplyChatPreviewIfNewer(
                            chat,
                            displayContent,
                            chatMessage.Timestamp,
                            false,
                            renderInfo?.PreviewKind,
                            listAuthorPrefix,
                            chatMessage.MentionedJids,
                            chatMessage.IsFromMe,
                            HistoryLiveMessageMapper.FromStatus(chatMessage.Status, chatMessage.IsFromMe),
                            chatMessage.Id);
                        foreach (var equivalentRow in GetChatRowsForCanonicalJid(jid))
                        {
                            if (!ReferenceEquals(equivalentRow, chat))
                            {
                                ApplyChatPreviewIfNewer(
                                    equivalentRow,
                                    displayContent,
                                    chatMessage.Timestamp,
                                    false,
                                    renderInfo?.PreviewKind,
                                    listAuthorPrefix,
                                    chatMessage.MentionedJids,
                                    chatMessage.IsFromMe,
                                    HistoryLiveMessageMapper.FromStatus(chatMessage.Status, chatMessage.IsFromMe),
                                    chatMessage.Id);
                            }
                        }

                        // If it's a 1-on-1 and name is still a number/JID, try to resolve it with the newly updated name
                        if (!isGroup && (chat.Name.Contains("@") || chat.Name == jid.Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(chat.Name)))
                        {
                            var resolvedChatName = ResolveDisplayName(jid, "chat");
                            if (!string.IsNullOrEmpty(resolvedChatName) && !resolvedChatName.Contains("@"))
                            {
                                chat.Name = resolvedChatName;
                                Log($"[WhatsAppService] Resolved name for UI chat {jid} -> {resolvedChatName}");
                            }
                        }
                        
                        // Keep pinned chats above regular chats while still moving
                        // the updated conversation to its correct real-time position.
                        RepositionChatForDisplay(chat);
                        
                        // Increment unread only when the conversation is not being
                        // viewed. Messages received in the open chat are already visible
                        // and should not create a badge or toast for themselves.
                        if (!isActuallyFromMe && !IsActiveChatJid(jid))
                        {
                            var unreadRows = GetChatRowsForCanonicalJid(jid);
                            int nextUnread = unreadRows.Count == 0
                                ? Math.Max(0, chat.UnreadCount) + 1
                                : unreadRows.Max(row => Math.Max(0, row.UnreadCount)) + 1;
                            foreach (var unreadRow in unreadRows)
                            {
                                unreadRow.UnreadCount = nextUnread;
                            }
                            chat.UnreadCount = nextUnread;
                        }

                        notificationChat = chat;
                    });

                SetIncomingMessagePumpStage("notify", e);
                if (!isActuallyFromMe)
                {
                    string notificationName = notificationChat?.Name;
                    if (string.IsNullOrWhiteSpace(notificationName))
                    {
                        notificationName = ResolveDisplayName(jid, "notification");
                    }

                    // Unified mute (WhatsApp sync + local SQLite) via MutedUntil.
                    if (notificationChat != null)
                    {
                        _chatStore?.ApplyTo(notificationChat);
                    }

                    bool isMuted = notificationChat != null
                        ? notificationChat.IsMutedLocally
                        : (_chatStore?.TryGetCached(jid)?.IsMutedLocally ?? false);
                    bool suppressToast = Unison.Uwp.App.IsWindowVisible && IsActiveChatJid(jid);

                    NotificationService.Instance.NotifyIncomingMessage(
                        jid,
                        notificationName,
                        senderName,
                        content,
                        isGroup,
                        isMuted,
                        suppressToast,
                        GetTotalUnreadCount(),
                        notificationChat?.GetAvatarUrl(preferHigh: false),
                        notificationChat != null ? Math.Max(0, notificationChat.UnreadCount) : 0);
                }

                SetIncomingMessagePumpStage("persist-queue", e);
                // Persistencia em lote: evita reler, serializar e reescrever o JSON
                // inteiro para cada mensagem recebida.
                QueueOfflineReplayMessageForPersist(jid, chatMessage);
                SchedulePersist();
                UnloadMessageCacheIfInactive(jid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] HandleDecryptedMessageAsync error: {ex.Message}");
                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "handle-decrypted-failed",
                    ex,
                    "id=" + (e?.MessageId ?? "<none>") + "; stage=" + _incomingMessagePumpStage);
                throw;
            }
        }

        private void RecordOfflineReplayChatSummary(
            string jid,
            string preview,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            bool countUnread,
            ChatPreviewKind kind = ChatPreviewKind.Text)
        {
            string canonical = GetCanonicalJid(NormalizeJid(jid));
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            DateTime comparableTimestamp = IsValidMessageTimestamp(timestamp)
                ? ToComparableUtc(timestamp)
                : DateTime.MinValue;

            lock (_offlineReplayUiLock)
            {
                if (!_offlineReplayUiSummaries.TryGetValue(canonical, out var summary))
                {
                    summary = new OfflineReplayChatSummary
                    {
                        Jid = canonical,
                        Timestamp = DateTime.MinValue,
                        IsGroup = isGroup,
                        Kind = ChatPreviewKind.Text
                    };
                    _offlineReplayUiSummaries[canonical] = summary;
                }

                if (comparableTimestamp != DateTime.MinValue &&
                    (summary.Timestamp == DateTime.MinValue || comparableTimestamp >= summary.Timestamp))
                {
                    summary.Timestamp = comparableTimestamp;
                    summary.Preview = preview ?? string.Empty;
                    summary.IsGroup = isGroup;
                    summary.IsFromMe = isFromMe;
                    summary.Kind = kind;
                }

                if (countUnread && !isFromMe && !IsActiveChatJid(canonical))
                {
                    summary.UnreadDelta++;
                }

                // Throttle instead of debounce: show the first recovered conversation
                // within ~180 ms even while a long replay continues. Further messages
                // schedule the next small UI batch after the current timer is consumed.
                if (_offlineReplayUiTimer == null)
                {
                    _offlineReplayUiTimer = new System.Threading.Timer(async _ =>
                    {
                        try
                        {
                            await ApplyOfflineReplayChatSummariesAsync("replay-progressive");
                        }
                        catch (Exception ex)
                        {
                            RuntimeDiagnosticsService.Instance.RecordException(
                                "messages",
                                "offline-summary-apply-failed",
                                ex,
                                "reason=replay-progressive");
                        }
                    }, null, (int)OfflineReplayUiDebounce.TotalMilliseconds, Timeout.Infinite);
                }
            }
        }

        private async Task ApplyOfflineReplayChatSummariesAsync(string reason)
        {
            await _offlineReplayUiApplyLock.WaitAsync();
            Dictionary<string, OfflineReplayChatSummary> snapshot = null;
            try
            {
                lock (_offlineReplayUiLock)
                {
                    if (_offlineReplayUiSummaries.Count == 0)
                    {
                        return;
                    }

                    snapshot = _offlineReplayUiSummaries.ToDictionary(
                        pair => pair.Key,
                        pair => new OfflineReplayChatSummary
                        {
                            Jid = pair.Value.Jid,
                            Preview = pair.Value.Preview,
                            Timestamp = pair.Value.Timestamp,
                            IsGroup = pair.Value.IsGroup,
                            IsFromMe = pair.Value.IsFromMe,
                            UnreadDelta = pair.Value.UnreadDelta,
                            Kind = pair.Value.Kind
                        },
                        StringComparer.OrdinalIgnoreCase);

                    _offlineReplayUiSummaries.Clear();
                    _offlineReplayUiTimer?.Dispose();
                    _offlineReplayUiTimer = null;
                }

                await RunOnUiThreadAsync(() =>
                {
                    int created = 0;
                    int updated = 0;
                    int unreadAdded = 0;

                    foreach (var pair in snapshot)
                    {
                        var summary = pair.Value;
                        if (summary == null || string.IsNullOrWhiteSpace(summary.Jid))
                        {
                            continue;
                        }

                        var rows = GetChatRowsForCanonicalJid(summary.Jid);
                        ChatItem preferred = rows.FirstOrDefault();
                        if (preferred == null)
                        {
                            preferred = new ChatItem
                            {
                                JID = summary.Jid,
                                Name = ResolveDisplayName(summary.Jid, "chat"),
                                Kind = ResolveChatKind(summary.Jid),
                                UnreadCount = 0
                            };
                            Chats.Add(preferred);
                            rows = GetChatRowsForCanonicalJid(summary.Jid);
                            created++;
                        }

                        foreach (var row in rows)
                        {
                            ApplyChatKind(row);
                            if (summary.Timestamp != DateTime.MinValue &&
                                ApplyChatPreviewIfNewer(
                                    row,
                                    summary.Preview ?? string.Empty,
                                    summary.Timestamp,
                                    false,
                                    summary.Kind,
                                    null,
                                    null,
                                    summary.IsFromMe,
                                    summary.IsFromMe
                                        ? MessageSendState.Sent
                                        : MessageSendState.NotApplicable))
                            {
                                updated++;
                            }
                        }

                        if (summary.UnreadDelta > 0 && !IsActiveChatJid(summary.Jid))
                        {
                            int currentUnread = rows.Count == 0
                                ? Math.Max(0, preferred.UnreadCount)
                                : rows.Max(row => Math.Max(0, row.UnreadCount));
                            int nextUnread = currentUnread + summary.UnreadDelta;
                            foreach (var row in rows)
                            {
                                row.UnreadCount = nextUnread;
                            }
                            preferred.UnreadCount = nextUnread;
                            unreadAdded += summary.UnreadDelta;
                        }
                    }

                    SortChatsForDisplay();
                    NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());

                    RuntimeDiagnosticsService.Instance.Write(
                        "messages",
                        "offline-summary-applied",
                        "reason=" + reason +
                        "; chats=" + snapshot.Count +
                        "; created=" + created +
                        "; previews=" + updated +
                        "; unreadAdded=" + unreadAdded);
                });

                SchedulePersist();
            }
            catch
            {
                if (snapshot != null)
                {
                    lock (_offlineReplayUiLock)
                    {
                        foreach (var pair in snapshot)
                        {
                            if (!_offlineReplayUiSummaries.TryGetValue(pair.Key, out var current))
                            {
                                _offlineReplayUiSummaries[pair.Key] = pair.Value;
                                continue;
                            }

                            if (pair.Value.Timestamp > current.Timestamp)
                            {
                                current.Timestamp = pair.Value.Timestamp;
                                current.Preview = pair.Value.Preview;
                                current.IsGroup = pair.Value.IsGroup;
                            }
                            current.UnreadDelta += pair.Value.UnreadDelta;
                        }
                    }
                }
                throw;
            }
            finally
            {
                _offlineReplayUiApplyLock.Release();
            }
        }

        private void MarkOfflineReplayChatDirty(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            lock (_offlineReplayPersistLock)
            {
                _offlineReplayDirtyChats.Add(jid);
            }
        }

        private async Task RefreshChatPreviewFromReplayAsync(
            string jid,
            string displayContent,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            ChatPreviewKind? kindHint = null)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
                {
                    var rows = GetChatRowsForCanonicalJid(jid);
                    if (rows.Count == 0)
                    {
                        return;
                    }

                    ChatItem preferred = null;
                    foreach (var row in rows)
                    {
                        if (ApplyChatPreviewIfNewer(
                            row,
                            displayContent,
                            timestamp,
                            false,
                            kindHint,
                            null,
                            null,
                            isFromMe,
                            isFromMe ? MessageSendState.Sent : MessageSendState.NotApplicable))
                        {
                            preferred = preferred ?? row;
                        }
                    }

                    if (preferred != null)
                    {
                        int index = Chats.IndexOf(preferred);
                        if (index > 0)
                        {
                            Chats.Move(index, 0);
                        }
                    }

                    Log($"[WhatsAppService] Replay preview refresh applied for {jid} at {timestamp:O}");
                });
        }

        /// <summary>
        /// Refreshes all chat previews from stored messages in a single UI dispatch.
        /// Called once after the offline batch drain completes, instead of per-message
        /// UI dispatches during the drain.
        /// </summary>
        private async Task RefreshAllChatPreviewsFromStoredAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
            {
                int updated = 0;
                foreach (var chat in Chats)
                {
                    string canonicalJid = GetCanonicalJid(chat.JID);
                    if (!MessagesByChat.TryGetValue(canonicalJid, out var messages) || messages == null || messages.Count == 0)
                    {
                        continue;
                    }

                    var latest = messages
                        .Where(m => m != null && IsValidMessageTimestamp(m.Timestamp))
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    bool isGroup = canonicalJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                    string preview = ChatPreviewNormalizer.FormatListPreview(latest, isGroup);
                    string author = ChatPreviewNormalizer.FormatListAuthorPrefix(latest, isGroup, SelfListDisplayName());

                    if (ApplyChatPreviewIfNewer(
                        chat,
                        preview,
                        latest.Timestamp,
                        false,
                        ChatPreviewNormalizer.InferKindFromMessage(latest),
                        author,
                        latest.MentionedJids,
                        latest.IsFromMe,
                        HistoryLiveMessageMapper.FromStatus(latest.Status, latest.IsFromMe),
                        latest.Id))
                    {
                        updated++;
                    }
                }

                Debug.WriteLine($"[WhatsAppService] Bulk preview refresh ({reason}): updated {updated} chat previews");
            });
        }

        private async Task ReconcileChatListFromStoredMessagesAsync(string reason)
        {
            await RunOnUiThreadAsync(() =>
            {
                int refreshed = 0;
                int created = 0;
                var latestByChat = new List<Tuple<ChatItem, DateTime>>();

                foreach (var kvp in MessagesByChat)
                {
                    string canonicalJid = GetCanonicalJid(kvp.Key);
                    if (string.IsNullOrWhiteSpace(canonicalJid) || kvp.Value == null || kvp.Value.Count == 0)
                    {
                        continue;
                    }

                    var latest = kvp.Value
                        .Where(m => m != null && IsValidMessageTimestamp(m.Timestamp))
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonicalJid);
                    if (chat == null)
                    {
                        chat = new ChatItem
                        {
                            JID = canonicalJid,
                            Name = ResolveDisplayName(canonicalJid, "chat"),
                            Kind = ResolveChatKind(canonicalJid)
                        };
                        Chats.Add(chat);
                        created++;
                    }

                    string preview = latest.Content ?? string.Empty;
                    ApplyChatPreviewIfNewer(
                        chat,
                        preview,
                        latest.Timestamp,
                        false,
                        ChatPreviewNormalizer.InferKindFromMessage(latest),
                        ChatPreviewNormalizer.FormatListAuthorPrefix(latest, JidHelper.IsGroupJid(canonicalJid), SelfListDisplayName()),
                        latest.MentionedJids,
                        latest.IsFromMe,
                        HistoryLiveMessageMapper.FromStatus(latest.Status, latest.IsFromMe),
                        latest.Id);
                    ApplyChatKind(chat);

                    if (!chat.IsGroup && (chat.Name.Contains("@") || chat.Name == canonicalJid.Replace("@s.whatsapp.net", "").Replace("@lid", "") || IsSelfMarkerLabel(chat.Name)))
                    {
                        chat.Name = ResolveDisplayName(canonicalJid, "chat");
                    }

                    DateTime effectivePreviewTimestamp = chat.LastMessageTimestampUtc.HasValue
                        ? ToComparableUtc(chat.LastMessageTimestampUtc.Value)
                        : ToComparableUtc(latest.Timestamp);
                    latestByChat.Add(Tuple.Create(chat, effectivePreviewTimestamp));
                    refreshed++;
                }

                int targetIndex = 0;
                foreach (var entry in latestByChat.OrderByDescending(t => t.Item2))
                {
                    int currentIndex = Chats.IndexOf(entry.Item1);
                    if (currentIndex >= 0 && currentIndex != targetIndex)
                    {
                        Chats.Move(currentIndex, targetIndex);
                    }
                    targetIndex++;
                }

                Log($"[WhatsAppService] Reconciled {refreshed} chat previews from cached messages (created={created}, reason={reason})");
            });
        }
    }
}
