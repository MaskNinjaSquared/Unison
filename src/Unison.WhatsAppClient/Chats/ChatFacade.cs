// =============================================================================
// ChatFacade
//
// The conversation itself as a subject: pinned or not, read or not, there or
// gone.
//
// Pin and mark-read are two writes rather than one, and the two do different
// jobs. The app state patch is what the account agrees on - it moves the pin and
// clears the badge on the phone. The receipt is what the other party sees. Only
// sending the patch leaves contacts without blue ticks; only sending the receipt
// leaves the chat unread everywhere but here.
//
// For those two the local copy is written first and reverted on failure. The
// list has to react to a tap immediately, and the round trip through the server
// is not fast enough to be part of that. Delete is the exception: it removes the
// messages, so there is nothing to revert to, and it waits for the patch.
//
// Ports: rc14 chatModify({ pin }), chatModify({ markRead }),
// chatModify({ delete }) and readMessages
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Socket.AppState;
using Unison.Socket.Models;
using Unison.Socket.UseCases.Messages;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Chats
{
    public sealed class ChatFacade : IChatService
    {
        /// <summary>
        /// How far back a mark-read reaches. The server only needs enough of the tail to place
        /// the mark, and a chat opened after a long absence should not turn into a receipt for
        /// every message it holds.
        /// </summary>
        private const int MaxMarkReadMessages = 50;

        /// <summary>
        /// How much of the tail a delete names. RC14 sends the newest few: the range is there to
        /// place the deletion, not to enumerate what is being removed.
        /// </summary>
        private const int MaxDeleteRangeMessages = 5;

        private readonly IChatAccountActions _account;
        private readonly IAppStateLocalHost _localAppState;
        private readonly IJidResolver _jids;
        private readonly ChatStateStore _chatState;
        private readonly ChatRowIndex _chatRows;
        private readonly ActiveChat _activeChat;
        private readonly IOfflineReplayHost _offlineReplay;
        private readonly IContactService _contacts;
        private readonly INotificationService _notifications;
        private readonly IDispatcher _ui;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IChatStore _chatStore;
        private readonly IShortcutService _shortcuts;
        private readonly StoredChatPreviewRefresh _storedPreviews;
        private readonly ChatPreviewReconciler _sqlitePreviews;
        private readonly ChatListDeduper _deduper;
        private readonly IChatIdentityPersistence _identity;
        private readonly ChatListAliasMerge _aliasMerge;
        private readonly ISelfMarkerNaming _selfMarkers;
        private readonly IStringResources _strings;
        private readonly IDialogService _dialogs;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly IHistoryMessageStore _historyMessages;
        private readonly IHistoryChatPreviewStore _chatPreviews;
        private readonly IMessageStore _messageStore;
        private readonly IMissingMessageChatHost _missingMessages;
        private readonly HistoryOnDemandRegistry _historyRequests;
        private readonly PendingMessageQueue _pendingMessages;
        private readonly Func<IHistoryService> _resolveHistory;
        private readonly OfflineReplayTips _offlineReplayTips;

        /// <summary>Short enough that the first recovered chat shows while a long replay continues.</summary>
        private static readonly TimeSpan OfflineReplayTipThrottle = TimeSpan.FromMilliseconds(180);

        public ChatFacade(
            IChatAccountActions account,
            IAppStateLocalHost localAppState,
            IJidResolver jids,
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            ActiveChat activeChat,
            IOfflineReplayHost offlineReplay,
            IContactService contacts,
            INotificationService notifications,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            IChatStore chatStore,
            IShortcutService shortcuts,
            StoredChatPreviewRefresh storedPreviews,
            ChatPreviewReconciler sqlitePreviews,
            ChatListDeduper deduper,
            IChatIdentityPersistence identity,
            ChatListAliasMerge aliasMerge,
            ISelfMarkerNaming selfMarkers,
            IStringResources strings,
            IHistoryMessageStore historyMessages,
            IHistoryChatPreviewStore chatPreviews,
            IMessageStore messageStore,
            IMissingMessageChatHost missingMessages,
            HistoryOnDemandRegistry historyRequests,
            PendingMessageQueue pendingMessages,
            Func<IHistoryService> resolveHistory,
            IDialogService dialogs = null,
            IRuntimeDiagnostics diagnostics = null)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _localAppState = localAppState ?? throw new ArgumentNullException(nameof(localAppState));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _offlineReplay = offlineReplay ?? throw new ArgumentNullException(nameof(offlineReplay));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
            _notifications = notifications;
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _chatStore = chatStore ?? throw new ArgumentNullException(nameof(chatStore));
            _shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
            _storedPreviews = storedPreviews ?? throw new ArgumentNullException(nameof(storedPreviews));
            _sqlitePreviews = sqlitePreviews ?? throw new ArgumentNullException(nameof(sqlitePreviews));
            _deduper = deduper ?? throw new ArgumentNullException(nameof(deduper));
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _aliasMerge = aliasMerge ?? throw new ArgumentNullException(nameof(aliasMerge));
            _selfMarkers = selfMarkers ?? throw new ArgumentNullException(nameof(selfMarkers));
            _strings = strings ?? throw new ArgumentNullException(nameof(strings));
            _historyMessages = historyMessages ?? throw new ArgumentNullException(nameof(historyMessages));
            _chatPreviews = chatPreviews ?? throw new ArgumentNullException(nameof(chatPreviews));
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _missingMessages = missingMessages ?? throw new ArgumentNullException(nameof(missingMessages));
            _historyRequests = historyRequests ?? throw new ArgumentNullException(nameof(historyRequests));
            _pendingMessages = pendingMessages ?? throw new ArgumentNullException(nameof(pendingMessages));
            _resolveHistory = resolveHistory ?? throw new ArgumentNullException(nameof(resolveHistory));
            _dialogs = dialogs;
            _diagnostics = diagnostics;
            _offlineReplayTips = new OfflineReplayTips(
                _jids,
                _offlineReplay,
                ApplyOfflineReplayChatSummariesAsync,
                OfflineReplayTipThrottle);
        }

        public void RecordOfflineReplayTip(OfflineReplayTipRequest request)
        {
            _offlineReplayTips.Record(request);
        }

        /// <inheritdoc />
        public async Task ApplyIncomingJournalRecoveryPreviewsAsync(
            IEnumerable<KeyValuePair<string, ChatMessage>> latestByChat)
        {
            if (latestByChat == null)
            {
                return;
            }

            string selfFallback = _strings.Get("Chat_SelfFallbackName", "You");
            foreach (KeyValuePair<string, ChatMessage> item in latestByChat)
            {
                ChatMessage message = item.Value;
                ChatPreviewKind kind = ChatPreviewNormalizer.InferKindFromMessage(message);
                string preview = message?.Content;
                if (string.IsNullOrWhiteSpace(preview) && kind != ChatPreviewKind.Revoked)
                {
                    preview = MediaPreviewTag.ForKind(kind) ?? "[Message]";
                }

                bool isGroupMsg = JidHelper.IsGroupJid(item.Key);
                string stripParticipant = isGroupMsg
                    ? ParticipantStripIdentity.PreferForDisplay(
                        message?.ParticipantJid,
                        _jids.GetCanonicalJid(message?.ParticipantJid))
                    : null;

                await RefreshChatPreviewAsync(
                        item.Key,
                        preview,
                        message?.Timestamp ?? DateTime.MinValue,
                        message?.IsFromMe == true,
                        kind,
                        ChatPreviewNormalizer.FormatListAuthorPrefix(
                            message,
                            isGroupMsg,
                            selfFallback,
                            stripParticipant),
                        stripParticipant,
                        isGroupMsg && message?.IsFromMe != true ? message?.SenderName : null)
                    .ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public Task ApplyIncomingEnvelopePreviewAsync(IncomingEnvelopePreviewRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Jid))
            {
                return Task.CompletedTask;
            }

            if (request.IsOffline)
            {
                if (request.MarkDirtyWhenOffline)
                {
                    _pendingMessages.MarkDirty(request.Jid);
                }

                RecordOfflineReplayTip(new OfflineReplayTipRequest
                {
                    Jid = request.Jid,
                    Preview = request.Preview,
                    Timestamp = request.Timestamp,
                    IsGroup = request.IsGroup,
                    IsFromMe = request.IsFromMe,
                    CountUnread = request.CountUnread,
                    Kind = request.Kind ?? ChatPreviewKind.Text,
                    Status = request.Status,
                    AuthorPrefix = request.AuthorPrefix,
                    ParticipantJid = request.ParticipantJid,
                    SenderName = request.SenderName
                });
                return Task.CompletedTask;
            }

            return RefreshChatPreviewAsync(
                request.Jid,
                request.Preview,
                request.Timestamp,
                request.IsFromMe,
                request.Kind,
                request.AuthorPrefix,
                request.ParticipantJid,
                request.SenderName);
        }

        public Task FlushOfflineReplayTipsAsync(string reason)
        {
            return _offlineReplayTips.FlushAsync(reason);
        }

        /// <summary>
        /// Account-side chat settings from socket app-state (archive / pin / mute / unread).
        /// </summary>
        public async Task ApplyRemoteChatSettingsAsync(ChatUpdate update)
        {
            if (update == null || string.IsNullOrEmpty(update.Id))
            {
                return;
            }

            AppStateMuteMapping.Result mute = AppStateMuteMapping.FromChatUpdate(
                update.AppliesMute,
                update.MuteEndTime);
            AppStatePinMapping.Result pin = AppStatePinMapping.FromChatUpdate(
                update.AppliesPin,
                update.Pinned,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (update.Archived.HasValue || pin.AppliesPin || mute.AppliesMute)
            {
                await ApplyLocalChatFlagsAsync(
                    update.Id,
                    archived: update.Archived,
                    pinned: pin.AppliesPin ? pin.IsPinned : (bool?)null,
                    muteEndTimestamp: mute.MutedUntilUnixSeconds,
                    pinnedTimestamp: pin.AppliesPin ? pin.PinnedTimestampUnixSeconds : (long?)null,
                    applyMute: mute.AppliesMute).ConfigureAwait(false);
            }

            if (update.UnreadCount.HasValue)
            {
                await ApplyLocalChatReadStateAsync(
                    update.Id,
                    read: update.UnreadCount.Value == 0).ConfigureAwait(false);
            }
        }

        public async Task SetPinnedAsync(ChatItem chat, bool pinned)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            // RC14's chatModify writes the patch under the chat id exactly as the caller holds
            // it - it never rewrites PN to LID. The canonical JID is the same id the local row
            // and the mark-read path use, so the pin lands in the collection the phone reads.
            var canonicalJid = _jids.GetCanonicalJid(chat.JID);
            var wasPinned = chat.IsChatPinned;

            await ApplyLocalChatFlagsAsync(
                canonicalJid,
                pinned: pinned,
                pinnedTimestamp: pinned
                    ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    : null).ConfigureAwait(false);

            if (!_account.IsAvailable)
            {
                Debug.WriteLine("[ChatFacade] Pin kept locally: no socket to tell the account");
                return;
            }

            try
            {
                await _account.SetPinnedAsync(canonicalJid, pinned).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Leaving the local state ahead of the account would show a pin that survives a
                // reinstall in name only, and would sort the list against what every other
                // device shows.
                var details =
                    "chatJid=" + chat.JID +
                    "; canonicalJid=" + canonicalJid +
                    "; requestedPinned=" + pinned +
                    "; previousPinned=" + wasPinned;

                Debug.WriteLine(
                    "[ChatFacade] Pin failed; " + details + Environment.NewLine + ex);
                DiagRecordException(
                    "app-state",
                    "chat-pin-patch-failed",
                    ex,
                    details);

                try
                {
                    await ApplyLocalChatFlagsAsync(
                        canonicalJid,
                        pinned: wasPinned,
                        pinnedTimestamp: wasPinned
                            ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            : null).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    var rollbackDetails = details + "; rollbackPinned=" + wasPinned;
                    Debug.WriteLine(
                        "[ChatFacade] Pin rollback failed; " + rollbackDetails +
                        Environment.NewLine + rollbackEx);
                    DiagRecordException(
                        "app-state",
                        "chat-pin-rollback-failed",
                        rollbackEx,
                        rollbackDetails);
                }

                throw;
            }
        }

        /// <summary>
        /// Account-side read/unread onto every alias row, then badge / shortcut / catalog slice.
        /// </summary>
        private async Task ApplyLocalChatReadStateAsync(string jid, bool read)
        {
            string canonical = _jids.GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<ChatItem> dirty = null;
            int resolved = 0;
            await _ui.RunAsync(() =>
            {
                var rows = AppStateChatRows.GetOrAdd(
                    _chatState.Chats,
                    _chatRows.FindConversation(canonical),
                    canonical,
                    address => _contacts.ResolveDisplayName(address, "app-state-read"),
                    address => JidHelper.ResolveKind(address, _jids.IsSelfLinked(address)),
                    out ChatItem created);
                if (created != null)
                {
                    dirty = new List<ChatItem> { created };
                }

                resolved = AppStateChatMutation.StampUnread(rows, read, ref dirty);
            }).ConfigureAwait(false);

            if (dirty == null || dirty.Count == 0)
            {
                return;
            }

            ChatUnreadSideEffects.AfterConversationUnreadChanged(
                GetTotalUnreadCount(),
                canonical,
                resolved,
                dirty,
                total => _notifications?.UpdateBadge(total),
                (shortcutJid, unread) =>
                {
                    try
                    {
                        _shortcuts.UpdateChatUnread(shortcutJid, unread);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatFacade] Shortcut unread update failed: " + ex.Message);
                    }
                },
                rows => _catalog.PersistRows(rows));
        }

        /// <summary>
        /// Archive / pin / mute onto rows and durable mirrors. Null fields are left alone.
        /// </summary>
        private async Task ApplyLocalChatFlagsAsync(
            string jid,
            bool? archived = null,
            bool? pinned = null,
            long? muteEndTimestamp = null,
            long? pinnedTimestamp = null,
            bool applyMute = false)
        {
            string canonical = _jids.GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            var change = new ChatFlagChange
            {
                Archived = archived,
                Pinned = pinned,
                PinnedTimestamp = pinnedTimestamp,
                MuteEndTimestamp = muteEndTimestamp,
                AppliesMute = applyMute
            };

            List<ChatItem> touched = null;
            await _ui.RunAsync(() =>
            {
                var rows = AppStateChatRows.GetOrAdd(
                    _chatState.Chats,
                    _chatRows.FindConversation(canonical),
                    canonical,
                    address => _contacts.ResolveDisplayName(address, "app-state-flags"),
                    address => JidHelper.ResolveKind(address, _jids.IsSelfLinked(address)),
                    out _);

                long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                touched = new List<ChatItem>();
                foreach (ChatItem chat in rows)
                {
                    AppStateChatMutation.ApplyFlags(chat, change, nowUnixMs);
                    touched.Add(chat);
                }

                ChatDisplayOrder.SortInPlace(_chatState.Chats);
            }).ConfigureAwait(false);

            if (touched != null && change.TouchesAnything)
            {
                foreach (ChatItem chat in touched)
                {
                    try
                    {
                        if (change.Pinned.HasValue)
                        {
                            string alias = null;
                            string normalizedRow = JidHelper.Normalize(chat.JID);
                            if (!string.IsNullOrWhiteSpace(normalizedRow))
                            {
                                _jids.TryGetAlias(normalizedRow, out alias);
                            }

                            IReadOnlyList<string> pinKeys = ChatPinStoreKeys.Merge(
                                ChatPinStoreKeys.For(chat.JID, _jids.GetCanonicalJid(chat.JID), alias),
                                ChatPinStoreKeys.For(jid, _jids));
                            for (int i = 0; i < pinKeys.Count; i++)
                            {
                                await _chatStore.SetChatPinnedAsync(pinKeys[i], change.Pinned.Value)
                                    .ConfigureAwait(false);
                            }
                        }

                        if (change.AppliesMute)
                        {
                            await _chatStore.SetMutedUntilAsync(chat.JID, change.MuteEndTimestamp)
                                .ConfigureAwait(false);
                        }

                        if (change.Archived.HasValue)
                        {
                            await _chatStore.SetStatusAsync(chat.JID, chat.Status)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatFacade] ChatStore upsert from app-state failed: " + ex.Message);
                    }
                }
            }

            if (archived.HasValue && touched != null && touched.Count > 0)
            {
                ChatStatus status = archived.Value ? ChatStatus.Archived : ChatStatus.Active;
                try
                {
                    await _chatPreviews.SetStatusAsync(
                            new List<string>(ChatPinStoreKeys.For(canonical, _jids)),
                            status)
                        .ConfigureAwait(false);
                    _catalog.PersistRows(touched);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ChatFacade] Archive status persist failed: " + ex.Message);
                }
            }

            _catalog.Schedule();
        }

        public async Task MarkReadAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            var unread = chat.UnreadCount;
            var jid = _jids.GetCanonicalJid(chat.JID);

            // Cleared first, and unconditionally: the badge is the part the user is looking at, it
            // should not wait for a round trip to disappear, and a PN/LID alias can leave a second
            // row carrying a count this one no longer has.
            await ClearUnreadForChatAsync(jid).ConfigureAwait(false);

            if (!_account.IsAvailable || unread <= 0)
            {
                return;
            }

            var recent = CollectTail(jid, Math.Min(MaxMarkReadMessages, Math.Max(unread + 1, 1)));
            if (recent.Count == 0)
            {
                return;
            }

            try
            {
                var incoming = recent.Where(m => !m.IsFromMe).ToList();
                if (incoming.Count > 0)
                {
                    await _account.MarkMessagesReadAsync(incoming.Select(ToReceiptTarget)).ConfigureAwait(false);
                }

                await _account.MarkChatReadAsync(jid, recent.Select(m => ToRangeMessage(jid, m))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The badge stays cleared. It is the local reading of a fact the user already
                // acted on, and putting it back because the network was busy would be worse than
                // being briefly out of step with the phone.
                Debug.WriteLine("[ChatFacade] Mark read failed: " + ex.GetBaseException().Message);
            }
        }

        public async Task DeleteChatAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            var jid = _jids.GetCanonicalJid(chat.JID);

            // The range is read before anything is removed: it names the tail the deletion covers,
            // and after the local wipe there is nothing left to describe.
            var range = CollectTail(jid, MaxDeleteRangeMessages);

            if (_account.IsAvailable && range.Count > 0)
            {
                try
                {
                    await _account
                        .DeleteChatAsync(jid, range.Select(m => ToRangeMessage(jid, m)))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Nothing was removed yet, so the chat is still intact and the user can retry.
                    // Wiping it here would leave this device disagreeing with the account forever:
                    // the deletion is never reported back as a state that could repair it.
                    var details =
                        "chatJid=" + chat.JID +
                        "; canonicalJid=" + jid +
                        "; rangeCount=" + range.Count;

                    Debug.WriteLine("[ChatFacade] Delete failed; " + details + Environment.NewLine + ex);
                    DiagRecordException(
                        "app-state",
                        "chat-delete-patch-failed",
                        ex,
                        details);

                    throw;
                }
            }
            else
            {
                // Offline, or a chat with no message to anchor the range - RC14's chatModify
                // requires lastMessages, so there is nothing valid to send. The chat goes away
                // here and the phone keeps it.
                Debug.WriteLine(
                    "[ChatFacade] Delete kept local: " +
                    (!_account.IsAvailable ? "no socket" : "no messages to build the range with"));
            }

            await ApplyAppStateDeleteChatAsync(jid).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ApplyAppStateDeleteChatAsync(string jid)
        {
            string canonical = _jids.GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<string> keys = new List<string>(ChatPinStoreKeys.For(canonical, _jids));

            await _ui.RunAsync(() =>
            {
                foreach (ChatItem chat in _chatRows.FindConversation(canonical))
                {
                    if (chat != null)
                    {
                        _chatState.Chats.Remove(chat);
                    }
                }

                _chatState.MessagesByChat.Remove(canonical);
                _missingMessages.ForgetMissingMessagesForChat(canonical);
                _historyRequests.ForgetChat(canonical);
            }).ConfigureAwait(false);

            await _localAppState.ForgetChatStorageAsync(canonical, keys).ConfigureAwait(false);

            ChatUnreadSideEffects.AfterConversationUnreadChanged(
                ChatUnreadTally.TotalAcrossConversations(_chatState.Chats, _jids.GetCanonicalJid),
                canonicalJid: null,
                chatUnread: 0,
                dirtyRows: null,
                total => _notifications?.UpdateBadge(total),
                updateShortcutUnread: null,
                persistRows: null);
            _offlineReplay.SchedulePersist();
        }

        // ---------------------------------------------------------------------
        // Local state of the list
        //
        // None of these talk to the account. They are the parts of "the chat as a whole" that the
        // UI asks about without changing anything the phone would need to hear, and they forward
        // to the client because that is still where the rows live (phase 3.9).
        // ---------------------------------------------------------------------

        public void StartNewChat(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return;
            }

            string normalized = JidHelper.Normalize(jid);
            _ = _ui.RunAsync(() =>
            {
                foreach (ChatItem chat in _chatState.Chats)
                {
                    if (chat != null && string.Equals(JidHelper.Normalize(chat.JID), normalized, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                _chatState.Chats.Insert(0, new ChatItem
                {
                    JID = normalized,
                    Name = _contacts.ResolveDisplayName(jid, "chat"),
                    LastMessage = "",
                    Timestamp = "",
                    Kind = JidHelper.ResolveKind(normalized, _jids.IsSelfLinked(normalized))
                });
                RequestChatListDedup("start-new-chat");
            });
        }

        public void SetActiveChatJid(string jid)
        {
            _activeChat.Open(jid);
        }

        /// <remarks>
        /// Memory first, then only the rows that changed go to disk: a full catalogue save here
        /// rewrote every chat preview, which took seconds on Mobile eMMC.
        /// </remarks>
        public async Task ClearUnreadForChatAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<ChatItem> changed = null;
            await _ui.RunAsync(() => changed = ChatUnreadTally.Clear(_chatRows.FindConversation(canonical)));
            if (changed == null || changed.Count == 0)
            {
                return;
            }

            ChatUnreadSideEffects.AfterConversationUnreadChanged(
                GetTotalUnreadCount(),
                canonical,
                chatUnread: 0,
                changed,
                total => _notifications?.UpdateBadge(total),
                (shortcutJid, unread) =>
                {
                    try
                    {
                        _shortcuts.UpdateChatUnread(shortcutJid, unread);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatFacade] Shortcut unread update failed: " + ex.Message);
                    }
                },
                rows => _catalog.PersistRows(rows));
        }

        public int GetTotalUnreadCount()
        {
            try
            {
                return ChatUnreadTally.TotalAcrossConversations(_chatState.Chats, _jids.GetCanonicalJid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] Unread total failed: " + ex.Message);
                return 0;
            }
        }

        /// <inheritdoc />
        public void SyncListStripFromTimeline(
            ChatItem chat,
            string requestedJid,
            IReadOnlyList<ChatMessage> visibleMessages)
        {
            if (chat == null)
            {
                return;
            }

            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
            string selfLabel = _strings.Get("Chat_SelfFallbackName", "You");
            ChatMessage tip = TimelineListStrip.PickTip(visibleMessages);
            if (tip == null)
            {
                _ = ReconcileChatPreviewsFromSqliteAsync(new[] { requestedJid }, "chat-open-empty");
                return;
            }

            bool isGroup = chat.IsGroup ||
                (chat.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            string rawPreview = ChatPreviewNormalizer.FormatListPreview(tip, isGroup);
            string stripParticipant = isGroup
                ? ParticipantStripIdentity.PreferForDisplay(
                    tip.ParticipantJid,
                    _jids.GetCanonicalJid(tip.ParticipantJid))
                : null;
            string authorPrefix = ChatPreviewNormalizer.FormatListAuthorPrefix(
                tip,
                isGroup,
                selfLabel,
                stripParticipant);
            ChatPreviewNormalizer.Normalize(
                rawPreview,
                ChatPreviewNormalizer.InferKindFromMessage(tip),
                out var previewKind,
                out var preview);
            MessageSendState sendState = HistoryLiveMessageMapper.FromStatus(tip.Status, tip.IsFromMe);
            string senderName = tip.IsFromMe ? null : tip.SenderName;

            if (TimelineListStrip.ShouldReplace(chat, tip, preview, previewKind, authorPrefix, sendState))
            {
                DateTime tipUtc = WhatsAppMapper.ToUtc(tip.Timestamp);
                chat.LastMessage = preview;
                chat.LastMessageAuthor = authorPrefix;
                chat.LastMessageKind = previewKind;
                chat.LastMessageMentionedJids = tip.MentionedJids != null && tip.MentionedJids.Count > 0
                    ? new List<string>(tip.MentionedJids)
                    : null;
                chat.LastMessageIsFromMe = tip.IsFromMe;
                chat.LastMessageSendState = sendState;
                chat.LastMessageId = tip.Id;
                chat.LastMessageTimestampUtc = tipUtc;
                chat.Timestamp = WhatsAppMapper.FormatTimestamp(tip.Timestamp, yesterday);
                LiveChatPreviewApplier.ApplyAuthorParts(chat, stripParticipant, senderName, authorPrefix);
                PersistChatListRows(new[] { chat });
                Debug.WriteLine(
                    "[ChatFacade] List preview from newest tip id=" + (tip.Id ?? "?") +
                    " fromMe=" + tip.IsFromMe + " ts=" + tipUtc.ToString("O"));
            }
            else if (isGroup &&
                     !string.IsNullOrWhiteSpace(tip.Id) &&
                     string.Equals(chat.LastMessageId, tip.Id, StringComparison.Ordinal))
            {
                LiveChatPreviewApplier.ApplyAuthorParts(chat, stripParticipant, senderName, authorPrefix);
                PersistChatListRows(new[] { chat });
            }

            _ = ReconcileChatPreviewsFromSqliteAsync(new[] { requestedJid }, "chat-open");
        }

        public void PersistChatListRows(IList<ChatItem> chats)
        {
            if (chats == null || chats.Count == 0)
            {
                return;
            }

            _catalog.PersistRows(chats);
        }

        public async Task ReconcileChatPreviewsFromSqliteAsync(
            IReadOnlyList<string> chatJids = null,
            string reason = null)
        {
            int updated = await _sqlitePreviews.ReconcileAsync(chatJids, reason).ConfigureAwait(false);
            if (updated <= 0)
            {
                return;
            }

            // Tip reconcile is local SQLite noise — do not stamp FULL_HISTORY catch-up
            // progress (that stuck "Synchronizing history…" on quiet accounts). Still
            // flush the catalogue when a tip actually moved.
            _offlineReplay.SchedulePersist();
        }

        public async Task RefreshChatPreviewAsync(
            string chatJid,
            string previewText,
            DateTime timestamp,
            bool isFromMe,
            ChatPreviewKind? kindHint = null,
            string authorPrefix = null,
            string participantJid = null,
            string senderName = null)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            await _ui.RunAsync(() =>
            {
                IReadOnlyList<ChatItem> rows = _chatRows.FindConversation(chatJid);
                if (rows == null || rows.Count == 0)
                {
                    return;
                }

                string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
                MessageSendState sendState = isFromMe
                    ? MessageSendState.Sent
                    : MessageSendState.NotApplicable;
                ChatItem preferred = null;

                foreach (ChatItem row in rows)
                {
                    if (row == null)
                    {
                        continue;
                    }

                    if (LiveChatPreviewApplier.ApplyIfNewer(
                        row,
                        previewText,
                        timestamp,
                        false,
                        kindHint,
                        authorPrefix,
                        mentionedJids: null,
                        isFromMe,
                        sendState,
                        messageId: null,
                        yesterday,
                        participantJid,
                        senderName))
                    {
                        preferred = preferred ?? row;
                    }
                }

                if (preferred != null)
                {
                    ChatDisplayOrder.Reposition(_chatState.Chats, preferred);
                }
            }).ConfigureAwait(false);
        }

        public async Task RefreshAllChatPreviewsFromStoredAsync(string reason)
        {
            int updated = await _storedPreviews.RefreshAsync(reason).ConfigureAwait(false);
            if (updated > 0)
            {
                Debug.WriteLine($"[ChatFacade] Bulk preview refresh ({reason}): updated {updated} chat previews");
            }
        }

        public async Task ReconcileChatListFromStoredAsync(string reason)
        {
            StoredChatPreviewReconcileResult result =
                await _storedPreviews.ReconcileAsync(reason).ConfigureAwait(false);
            Debug.WriteLine(
                $"[ChatFacade] Reconciled {result.Refreshed} chat previews from cached messages " +
                $"(created={result.Created}, reason={reason})");
        }

        /// <inheritdoc />
        public async Task NotifyPostReplayMaintenanceAsync(
            int offlineCount,
            bool runDelayedBulkRepair,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReconcileChatPreviewsFromSqliteAsync(null, "post-replay")
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ChatFacade] Post-replay preview reconcile failed: " + ex.Message);
            }

            if (!runDelayedBulkRepair)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await ReconcileChatListFromStoredAsync(
                "delayed-offline-repair:" + offlineCount).ConfigureAwait(false);
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            await RefreshAllChatPreviewsFromStoredAsync("delayed-post-offline-drain")
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task NotifyDeferredStartupListRepairAsync()
        {
            await DeduplicateChatListAsync("deferred-startup").ConfigureAwait(false);
            await RepairLegacyDeletedPreviewsAsync().ConfigureAwait(false);
            await ReconcileChatPreviewsFromSqliteAsync(null, "deferred-startup")
                .ConfigureAwait(false);
        }

        public Task MergeDuplicateChatsAsync(string lidJid, string pnJid)
        {
            return _aliasMerge.MergeAsync(lidJid, pnJid);
        }

        public void RequestAliasChatMerge(string lidJid, string pnJid)
        {
            _ = _aliasMerge.MergeAsync(lidJid, pnJid);
        }

        /// <inheritdoc />
        public async Task CollapseTransientDirectChatAsync(
            string transientJid,
            string canonicalJid,
            string reason)
        {
            ChatListTransientCollapseResult collapsed =
                await _aliasMerge.CollapseTransientAsync(transientJid, canonicalJid)
                    .ConfigureAwait(false);
            if (!collapsed.Merged)
            {
                return;
            }

            Debug.WriteLine(
                "[ChatFacade] Collapsed transient direct chat " + collapsed.TransientJid +
                " into " + collapsed.CanonicalJid + " (" + (reason ?? string.Empty) + ")");

            _missingMessages.ForgetMissingMessagesForChat(collapsed.TransientJid);

            if (collapsed.CanonicalMessages.Count > 0)
            {
                await _historyMessages.UpsertLiveMessagesAsync(
                        collapsed.CanonicalJid,
                        collapsed.CanonicalMessages)
                    .ConfigureAwait(false);
            }

            try
            {
                await _messageStore.DeleteChatMessagesAsync(collapsed.TransientJid)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[ChatFacade] Transient chat message delete failed: " + ex.Message);
            }

            _identity.PersistNow(reason ?? "transient-collapse");
        }

        public void RequestChatListDedup(string reason)
        {
            _ = DeduplicateChatListAsync(reason);
        }

        public Task DeduplicateChatListAsync(string reason) => RunChatListDedupAsync(reason);

        private async Task RunChatListDedupAsync(string reason)
        {
            ChatListDeduperResult result = await _deduper.RunAsync(reason ?? "facade").ConfigureAwait(false);
            if (result.Merged <= 0 && result.NormalizedKeys <= 0)
            {
                return;
            }

            Debug.WriteLine(
                $"[ChatFacade] Deduplicated {result.Merged} duplicate chat entries and normalized " +
                $"{result.NormalizedKeys} message caches (reason={reason})");
            _catalog.Schedule();
            _chatState.NotifyDisplayNamesChanged();
            _contacts.NotifyDisplayNamesUpdated();
            if (result.Merged > 0)
            {
                _identity.PersistNow("dedupe:" + (reason ?? "facade"));
            }
        }

        public async Task ShowLocalSendPreviewAsync(
            string chatJid,
            string preview,
            DateTime timestamp,
            ChatPreviewKind? kindHint = null,
            IList<string> mentionedJids = null,
            string messageId = null,
            string status = null)
        {
            string canonicalJid = _jids.GetCanonicalJid(JidHelper.Normalize(chatJid));
            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
            await _ui.RunAsync(() =>
            {
                // PN and LID may briefly be two rows; tip every alias, then lift the preferred one.
                LocalSendChatPreview.ApplyToAliasRows(
                    _chatState.Chats,
                    _chatRows.FindConversation(canonicalJid),
                    canonicalJid,
                    () => _contacts.ResolveDisplayName(canonicalJid, "local-send"),
                    () => JidHelper.ResolveKind(canonicalJid, _jids.IsSelfLinked(canonicalJid)),
                    preview,
                    timestamp,
                    kindHint,
                    mentionedJids,
                    messageId,
                    HistoryLiveMessageMapper.FromStatus(status ?? ChatMessage.StatusPending, true),
                    yesterday);
            }).ConfigureAwait(false);

            RequestChatListDedup("local-send-preview");
            _catalog.Schedule();
        }

        public async Task RepairLegacyDeletedPreviewsAsync()
        {
            // Builds before v5.14 could persist a replayed revoke envelope as a new
            // "[Message Deleted]" row with DateTime.Now. Repair only those known-bad
            // tips and leave normal chats untouched.
            List<ChatItem> suspicious = null;
            await _ui.RunAsync(() =>
            {
                suspicious = new List<ChatItem>();
                foreach (ChatItem chat in _chatState.Chats)
                {
                    if (LegacyDeletedPreview.IsSuspect(chat))
                    {
                        suspicious.Add(chat);
                    }
                }
            }).ConfigureAwait(false);

            if (suspicious == null || suspicious.Count == 0)
            {
                return;
            }

            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
            string selfLabel = _strings.Get("Chat_SelfFallbackName", "You");
            bool changed = false;

            foreach (ChatItem chat in suspicious)
            {
                try
                {
                    string jid = _jids.GetCanonicalJid(chat.JID);
                    IReadOnlyList<HistoryMessage> recentRows =
                        await _historyMessages.GetForChatAsync(jid, 120).ConfigureAwait(false);
                    List<ChatMessage> mapped = null;
                    if (recentRows != null)
                    {
                        mapped = new List<ChatMessage>(recentRows.Count);
                        for (int i = 0; i < recentRows.Count; i++)
                        {
                            ChatMessage m = HistoryMessageMapper.ToChatMessage(recentRows[i]);
                            if (m != null)
                            {
                                mapped.Add(m);
                            }
                        }
                    }

                    ChatMessage replacement = LegacyDeletedPreview.PickReplacement(mapped, DateTime.UtcNow);

                    await _ui.RunAsync(() =>
                    {
                        IReadOnlyList<ChatItem> rows = _chatRows.FindConversation(jid);
                        for (int i = 0; i < rows.Count; i++)
                        {
                            ChatItem row = rows[i];
                            if (row == null)
                            {
                                continue;
                            }

                            if (replacement != null)
                            {
                                bool isGroup = jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                                    || (row.JID ?? string.Empty).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                                    || row.IsGroup;
                                LiveChatPreviewApplier.ApplyIfNewer(
                                    row,
                                    ChatPreviewNormalizer.FormatListPreview(replacement, isGroup),
                                    replacement.Timestamp,
                                    force: true,
                                    ChatPreviewNormalizer.InferKindFromMessage(replacement),
                                    ChatPreviewNormalizer.FormatListAuthorPrefix(
                                        replacement,
                                        isGroup,
                                        selfLabel,
                                        _jids.GetCanonicalJid(replacement.ParticipantJid)),
                                    replacement.MentionedJids,
                                    replacement.IsFromMe,
                                    HistoryLiveMessageMapper.FromStatus(replacement.Status, replacement.IsFromMe),
                                    replacement.Id,
                                    yesterday,
                                    isGroup
                                        ? ParticipantStripIdentity.PreferForDisplay(
                                            replacement.ParticipantJid,
                                            _jids.GetCanonicalJid(replacement.ParticipantJid))
                                        : null,
                                    isGroup && !replacement.IsFromMe ? replacement.SenderName : null);
                            }
                            else
                            {
                                ChatPreviewTip.Clear(row);
                            }
                        }

                        ChatDisplayOrder.SortInPlace(_chatState.Chats);
                    }).ConfigureAwait(false);
                    changed = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[ChatFacade] Legacy deleted-preview repair failed for " + chat.JID + ": " + ex.Message);
                }

                await Task.Yield();
            }

            if (!changed)
            {
                return;
            }

            try
            {
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() =>
                {
                    snapshot = new List<ChatItem>();
                    foreach (ChatItem c in _chatState.Chats)
                    {
                        if (c != null)
                        {
                            snapshot.Add(c);
                        }
                    }
                }).ConfigureAwait(false);
                _catalog.PersistRows(snapshot ?? new List<ChatItem>());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] Legacy preview catalogue persist failed: " + ex.Message);
            }

            try
            {
                _messageStore.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] ClearMemoryCache after legacy preview repair failed: " + ex.Message);
            }

            _resolveHistory()?.NotifyHistorySyncReceived(null);
            _offlineReplay.SchedulePersist();
        }

        public async Task<LiveIncomingChatListApplyResult> ApplyLiveIncomingChatListAsync(
            LiveIncomingChatListApplyRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ChatJid))
            {
                return new LiveIncomingChatListApplyResult();
            }

            int unreadDelta = request.UnreadDelta;
            if (unreadDelta == 0 && request.CountsAsUnread)
            {
                unreadDelta = 1;
            }

            LiveIncomingChatListApplyResult result = new LiveIncomingChatListApplyResult();
            await _ui.RunAsync(() =>
            {
                ChatItem chat = ApplyStripOnUiThread(
                    request.ChatJid,
                    request.PreviewText,
                    request.Timestamp,
                    request.PreviewKind,
                    request.AuthorPrefix,
                    request.MentionedJids,
                    request.IsFromMe,
                    request.SendState,
                    request.MessageId,
                    unreadDelta,
                    request.IsGroup,
                    request.AliasLid,
                    request.AliasPn,
                    createAtFront: true,
                    out _,
                    request.ParticipantJid,
                    request.SenderName);

                result.Chat = chat;
                result.DisplayName = chat != null ? chat.Name : null;
                result.TotalUnread = GetTotalUnreadCount();
            }).ConfigureAwait(false);

            return result;
        }

        /// <summary>Applies tips whose unread was already decided at record time.</summary>
        private async Task ApplyOfflineReplayChatSummariesAsync(
            IReadOnlyList<OfflineReplayChatSummary> summaries,
            string reason)
        {
            if (summaries == null || summaries.Count == 0)
            {
                return;
            }

            await _ui.RunAsync(() =>
            {
                int created = 0;
                int updated = 0;
                int unreadAdded = 0;

                foreach (OfflineReplayChatSummary summary in summaries)
                {
                    if (summary == null || string.IsNullOrWhiteSpace(summary.Jid))
                    {
                        continue;
                    }

                    bool createdRow;
                    ChatItem preferred = ApplyStripOnUiThread(
                        summary.Jid,
                        summary.Preview ?? string.Empty,
                        summary.Timestamp,
                        summary.Kind,
                        summary.AuthorPrefix,
                        mentionedJids: null,
                        summary.IsFromMe,
                        HistoryLiveMessageMapper.FromStatus(summary.Status, summary.IsFromMe),
                        messageId: null,
                        summary.UnreadDelta,
                        summary.IsGroup,
                        aliasLid: null,
                        aliasPn: null,
                        createAtFront: false,
                        out createdRow,
                        summary.ParticipantJid,
                        summary.SenderName);

                    if (createdRow)
                    {
                        created++;
                    }

                    if (preferred != null && summary.Timestamp != DateTime.MinValue)
                    {
                        updated++;
                    }

                    if (summary.UnreadDelta > 0)
                    {
                        unreadAdded += summary.UnreadDelta;
                    }
                }

                ChatDisplayOrder.SortInPlace(_chatState.Chats);
                int totalUnread = GetTotalUnreadCount();
                if (_notifications != null)
                {
                    _notifications.UpdateBadge(totalUnread);
                }

                DiagWrite(
                    "messages",
                    "offline-summary-applied",
                    "reason=" + (reason ?? string.Empty) +
                    "; chats=" + summaries.Count +
                    "; created=" + created +
                    "; previews=" + updated +
                    "; unreadAdded=" + unreadAdded);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Shared live / offline strip write. Caller must already be on the UI thread.
        /// </summary>
        private ChatItem ApplyStripOnUiThread(
            string jid,
            string previewText,
            DateTime timestamp,
            ChatPreviewKind? previewKind,
            string authorPrefix,
            IList<string> mentionedJids,
            bool isFromMe,
            MessageSendState sendState,
            string messageId,
            int unreadDelta,
            bool isGroup,
            string aliasLid,
            string aliasPn,
            bool createAtFront,
            out bool createdRow,
            string participantJid = null,
            string senderName = null)
        {
            createdRow = false;
            string canonicalLookup = _jids.GetCanonicalJid(jid) ?? jid;
            ChatItem chat = _chatState.Chats.FirstOrDefault(c =>
                c != null &&
                string.Equals(
                    _jids.GetCanonicalJid(c.JID),
                    canonicalLookup,
                    StringComparison.OrdinalIgnoreCase));

            if (chat == null)
            {
                string chatName = ResolveName(jid, "chat");
                chat = new ChatItem
                {
                    JID = _jids.GetCanonicalJid(jid) ?? jid,
                    Name = chatName,
                    Kind = JidHelper.ResolveKind(jid, _jids.IsSelfLinked(jid)),
                    UnreadCount = 0
                };
                if (createAtFront)
                {
                    _chatState.Chats.Insert(0, chat);
                }
                else
                {
                    _chatState.Chats.Add(chat);
                }

                createdRow = true;
                Debug.WriteLine("[ChatFacade] Created new chat entry for " + jid + " (" + chatName + ")");
                RequestChatListDedup(createAtFront ? "incoming-new-chat" : "offline-new-chat");

                if (!string.IsNullOrWhiteSpace(aliasLid) && !string.IsNullOrWhiteSpace(aliasPn))
                {
                    RequestAliasChatMerge(aliasLid, aliasPn);
                }

                if (PlaceholderChatLabel.IsPlaceholder(
                        chat.Name,
                        chat.JID,
                        _selfMarkers.IsMarkerLabel(chat.Name)))
                {
                    IContactService contacts = _contacts;
                    if (contacts != null)
                    {
                        _ = contacts.ResolveMissingNamesAsync();
                    }
                }
            }

            chat.ApplyKind(chat.JID, _jids.IsSelfLinked(chat.JID));

            string yesterday = _strings.Get("Common_Yesterday", "Yesterday");
            if (timestamp != DateTime.MinValue)
            {
                LiveChatPreviewApplier.ApplyIfNewer(
                    chat,
                    previewText,
                    timestamp,
                    false,
                    previewKind,
                    authorPrefix,
                    mentionedJids,
                    isFromMe,
                    sendState,
                    messageId,
                    yesterday,
                    participantJid,
                    senderName);

                foreach (ChatItem equivalentRow in _chatRows.FindConversation(jid))
                {
                    if (!ReferenceEquals(equivalentRow, chat))
                    {
                        equivalentRow.ApplyKind(equivalentRow.JID, _jids.IsSelfLinked(equivalentRow.JID));
                        LiveChatPreviewApplier.ApplyIfNewer(
                            equivalentRow,
                            previewText,
                            timestamp,
                            false,
                            previewKind,
                            authorPrefix,
                            mentionedJids,
                            isFromMe,
                            sendState,
                            messageId,
                            yesterday,
                            participantJid,
                            senderName);
                    }
                }
            }

            if (!isGroup &&
                PlaceholderChatLabel.IsPlaceholder(
                    chat.Name,
                    jid,
                    _selfMarkers.IsMarkerLabel(chat.Name)))
            {
                string resolvedChatName = ResolveName(jid, "chat");
                if (!string.IsNullOrEmpty(resolvedChatName) && !resolvedChatName.Contains("@"))
                {
                    chat.Name = resolvedChatName;
                }
            }

            if (createAtFront)
            {
                ChatDisplayOrder.Reposition(_chatState.Chats, chat);
            }

            if (unreadDelta > 0)
            {
                ChatUnreadTally.Bump(chat, _chatRows.FindConversation(jid), unreadDelta);
            }

            return chat;
        }

        private void DiagWrite(string category, string eventName, string details = null)
        {
            if (_diagnostics != null)
            {
                _diagnostics.Write(category, eventName, details);
            }
        }

        private void DiagRecordException(
            string category,
            string eventName,
            Exception exception,
            string details = null)
        {
            if (_diagnostics != null)
            {
                _diagnostics.RecordException(category, eventName, exception, details);
            }
        }

        private string ResolveName(string jid, string context) => _contacts.ResolveDisplayName(jid, context);

        /// <summary>
        /// The last <paramref name="take"/> messages of the conversation, oldest first. Both
        /// mark-read and delete send a range rather than a single id, and both only need the tail.
        /// </summary>
        private List<ChatMessage> CollectTail(string jid, int take)
        {
            List<ChatMessage> live;
            try
            {
                live = _chatState.GetConversationMessages(jid, _jids.GetCanonicalJid);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] Could not read the conversation: " + ex.Message);
                return new List<ChatMessage>();
            }

            if (live == null || live.Count == 0)
            {
                return new List<ChatMessage>();
            }

            return MessageTail.Addressable(live, take);
        }

        private static ReceiptTarget ToReceiptTarget(ChatMessage message)
        {
            return new ReceiptTarget
            {
                RemoteJid = message.RemoteJid,
                Id = message.Id,
                FromMe = message.IsFromMe,
                Participant = message.ParticipantJid
            };
        }

        private static RangeMessage ToRangeMessage(string jid, ChatMessage message)
        {
            return new RangeMessage
            {
                Id = message.Id,
                FromMe = message.IsFromMe,
                Participant = jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
                    ? message.ParticipantJid
                    : null,
                Timestamp = ToUnixSeconds(message.Timestamp)
            };
        }

        private static long ToUnixSeconds(DateTime timestamp)
        {
            if (timestamp == default(DateTime))
            {
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            var utc = Unison.Core.Mappers.WhatsAppMapper.ToUtc(timestamp);

            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        /// <inheritdoc />
        public HistoryChatPreviewHydrateResult ApplyHistoryChatPreviewHydrate(
            HistoryChatPreviewHydrateRequest request)
        {
            var result = new HistoryChatPreviewHydrateResult
            {
                CreatedChats = Array.Empty<ChatItem>()
            };
            if (request?.Rows == null || request.Rows.Count == 0)
            {
                return result;
            }

            string yesterday = request.YesterdayLabel;
            string selfLabel = string.IsNullOrWhiteSpace(request.SelfDisplayName)
                ? "You"
                : request.SelfDisplayName;
            var index = BuildHydrateIndex();
            var created = new List<ChatItem>();
            int updated = 0;

            foreach (HistoryChatPreview preview in request.Rows)
            {
                if (preview == null || string.IsNullOrWhiteSpace(preview.Jid))
                {
                    continue;
                }

                if (!HistoryChatPreviewApplier.IsListable(preview))
                {
                    if (preview.IsChatPinned.HasValue || preview.AppliesMute)
                    {
                        ChatItem flagTarget = FindInHydrateIndex(index, preview);
                        if (flagTarget != null)
                        {
                            HistoryChatPreviewApplier.ApplyLocalFlags(preview, flagTarget);
                            PersistHistoryLocalFlags(flagTarget, preview);
                            TryApplyCanonicalPinFallback(flagTarget);
                            updated++;
                        }
                        else
                        {
                            PersistHistoryLocalFlags(
                                new ChatItem { JID = preview.Jid },
                                preview);
                        }
                    }

                    continue;
                }

                ChatItem existing = FindInHydrateIndex(index, preview);
                if (existing == null)
                {
                    ChatItem row = HistoryChatPreviewApplier.ToChatItem(preview, yesterday, selfLabel);
                    if (row == null)
                    {
                        continue;
                    }

                    try
                    {
                        _chatStore.ApplyTo(row);
                        HistoryChatPreviewApplier.ApplyLocalFlags(preview, row);
                        if (preview.IsChatPinned.HasValue || preview.AppliesMute)
                        {
                            PersistHistoryLocalFlags(row, preview);
                        }

                        TryApplyCanonicalPinFallback(row);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatFacade] ApplyTo (preview create) failed: " + ex.Message);
                    }

                    _chatState.Chats.Add(row);
                    created.Add(row);
                    IndexHydrateChat(index, row);
                }
                else
                {
                    bool bodyChanged = HistoryChatPreviewApplier.ApplyIfNewer(
                        preview,
                        existing,
                        yesterday,
                        selfLabel);
                    try
                    {
                        if (bodyChanged)
                        {
                            updated++;
                        }

                        HistoryChatPreviewApplier.ApplyLocalFlags(preview, existing);
                        bool flagsChanged = preview.IsChatPinned.HasValue || preview.AppliesMute;
                        if (flagsChanged)
                        {
                            PersistHistoryLocalFlags(existing, preview);
                            if (!bodyChanged)
                            {
                                updated++;
                            }
                        }

                        if (bodyChanged || flagsChanged)
                        {
                            TryApplyCanonicalPinFallback(existing);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[ChatFacade] ApplyTo (preview update) failed: " + ex.Message);
                    }
                }
            }

            result.CreatedCount = created.Count;
            result.UpdatedCount = updated;
            result.CreatedChats = created;
            return result;
        }

        private Dictionary<string, ChatItem> BuildHydrateIndex()
        {
            var index = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatItem chat in _chatState.Chats)
            {
                IndexHydrateChat(index, chat);
            }

            return index;
        }

        private static void IndexHydrateChat(Dictionary<string, ChatItem> index, ChatItem chat)
        {
            if (index == null || chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            string key = JidHelper.Normalize(chat.JID);
            if (!string.IsNullOrWhiteSpace(key) && !index.ContainsKey(key))
            {
                index[key] = chat;
            }
        }

        private static ChatItem FindInHydrateIndex(
            Dictionary<string, ChatItem> index,
            HistoryChatPreview preview)
        {
            if (index == null || preview == null)
            {
                return null;
            }

            ChatItem found;
            if (TryHydrateLookup(index, preview.Jid, out found) ||
                TryHydrateLookup(index, preview.LidJid, out found) ||
                TryHydrateLookup(index, preview.PnJid, out found))
            {
                return found;
            }

            return null;
        }

        private static bool TryHydrateLookup(
            Dictionary<string, ChatItem> index,
            string jid,
            out ChatItem chat)
        {
            chat = null;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            string key = JidHelper.Normalize(jid);
            return !string.IsNullOrWhiteSpace(key) && index.TryGetValue(key, out chat);
        }

        private void PersistHistoryLocalFlags(ChatItem chat, HistoryChatPreview preview)
        {
            if (chat == null || preview == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            if (preview.IsChatPinned.HasValue)
            {
                RememberPinnedUnderAliases(chat.JID, preview.IsChatPinned.Value);
                if (!string.IsNullOrWhiteSpace(preview.LidJid))
                {
                    _chatStore.RememberChatPinned(preview.LidJid, preview.IsChatPinned.Value);
                }

                if (!string.IsNullOrWhiteSpace(preview.PnJid))
                {
                    _chatStore.RememberChatPinned(preview.PnJid, preview.IsChatPinned.Value);
                }

                _ = PersistHistoryPinnedAsync(chat.JID, preview.IsChatPinned.Value);
                if (!string.IsNullOrWhiteSpace(preview.LidJid))
                {
                    _ = PersistHistoryPinnedAsync(preview.LidJid, preview.IsChatPinned.Value);
                }

                if (!string.IsNullOrWhiteSpace(preview.PnJid))
                {
                    _ = PersistHistoryPinnedAsync(preview.PnJid, preview.IsChatPinned.Value);
                }
            }

            if (preview.AppliesMute)
            {
                _chatStore.RememberMutedUntil(chat.JID, preview.MutedUntil);
                _ = PersistHistoryMuteAsync(chat.JID, preview.MutedUntil);
            }
        }

        /// <inheritdoc />
        public void RememberPinnedUnderAliases(string jid, bool pinned)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            IReadOnlyList<string> keys = ChatPinStoreKeys.For(jid, _jids);
            for (int i = 0; i < keys.Count; i++)
            {
                _chatStore.RememberChatPinned(keys[i], pinned);
            }
        }

        /// <inheritdoc />
        public void ApplyLocalStateAliasFallback(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            ChatLocalState exact = _chatStore.TryGetCached(chat.JID);
            // An explicit unpin under this exact JID wins over any alias.
            if (exact != null && exact.Knows(ChatLocalStateFields.Pin) && !exact.IsChatPinned)
            {
                return;
            }

            IReadOnlyList<string> keys = ChatPinStoreKeys.For(chat.JID, _jids);
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.Equals(
                        JidHelper.Normalize(key),
                        JidHelper.Normalize(chat.JID),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ChatLocalState alt = _chatStore.TryGetCached(key);
                if (alt == null || !alt.Knows(ChatLocalStateFields.Pin) || !alt.IsChatPinned)
                {
                    continue;
                }

                if (!chat.IsChatPinned)
                {
                    chat.IsChatPinned = true;
                    if (chat.PinnedTimestamp == null || chat.PinnedTimestamp == 0)
                    {
                        chat.PinnedTimestamp = 1;
                    }

                    RememberPinnedUnderAliases(chat.JID, true);
                }

                break;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (string.Equals(
                        JidHelper.Normalize(key),
                        JidHelper.Normalize(chat.JID),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ChatLocalState alt = _chatStore.TryGetCached(key);
                if (alt == null)
                {
                    continue;
                }

                if (!chat.IsWidgetPinned &&
                    alt.Knows(ChatLocalStateFields.WidgetPin) &&
                    alt.IsWidgetPinned)
                {
                    chat.IsWidgetPinned = true;
                }

                if (chat.MutedUntil == null &&
                    !(exact != null && exact.Knows(ChatLocalStateFields.Mute)) &&
                    alt.Knows(ChatLocalStateFields.Mute) &&
                    alt.MutedUntil != null)
                {
                    chat.MutedUntil = alt.MutedUntil;
                    _chatStore.RememberMutedUntil(chat.JID, alt.MutedUntil);
                }
            }
        }

        private async Task PersistHistoryPinnedAsync(string jid, bool pinned)
        {
            try
            {
                IReadOnlyList<string> keys = ChatPinStoreKeys.For(jid, _jids);
                for (int i = 0; i < keys.Count; i++)
                {
                    await _chatStore.SetChatPinnedAsync(keys[i], pinned).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] Persist history pin failed: " + ex.Message);
            }
        }

        private async Task PersistHistoryMuteAsync(string jid, long? mutedUntil)
        {
            try
            {
                await _chatStore.SetMutedUntilAsync(jid, mutedUntil).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatFacade] Persist history mute failed: " + ex.Message);
            }
        }

        private void TryApplyCanonicalPinFallback(ChatItem chat)
        {
            ApplyLocalStateAliasFallback(chat);
        }

        /// <inheritdoc />
        public long MuteUntilEightHoursFromNow()
        {
            return ChatMuteHelper.FromNow(ChatMuteHelper.EightHours);
        }

        /// <inheritdoc />
        public long MuteUntilOneWeekFromNow()
        {
            return ChatMuteHelper.FromNow(ChatMuteHelper.OneWeek);
        }

        /// <inheritdoc />
        public long MuteForeverUnixSeconds => ChatMuteHelper.ForeverUnixSeconds;

        /// <inheritdoc />
        public Task<bool> ConfirmAndDeleteAsync(ChatItem chat)
        {
            return ChatDeletionPrompt.ConfirmAndDeleteAsync(chat, this, _dialogs, _strings);
        }

        /// <inheritdoc />
        public List<ChatItem> SortChatsForDisplay(IEnumerable<ChatItem> source)
        {
            return ChatListDisplayOrder.SortForDisplay(source);
        }

        /// <inheritdoc />
        public List<ChatItem> DeduplicateChatsByCanonicalJid(
            IEnumerable<ChatItem> source,
            Func<string, string> getCanonical)
        {
            return ChatListDisplayOrder.DeduplicateByCanonicalJid(source, getCanonical);
        }

        /// <inheritdoc />
        public List<ChatItem> ProjectCommunityList(
            IReadOnlyList<ChatItem> source,
            Func<string, ChatItem> resolveParent,
            Func<string, ChatItem> getOrCreateSynthetic,
            Func<string, string> getCanonical)
        {
            return CommunityListProjector.Project(
                source,
                resolveParent,
                getOrCreateSynthetic,
                getCanonical,
                _strings);
        }

        /// <inheritdoc />
        public bool MatchesChatListQuery(
            ChatItem chat,
            string query,
            Func<string> resolveChatName,
            Func<string, string> resolveMentionName)
        {
            return ChatListQuery.Matches(chat, query, resolveChatName, resolveMentionName);
        }

        /// <inheritdoc />
        public bool ShouldArmDeepLinkListHold(bool appWasAlreadyOpen) =>
            ChatListDeepLinkSyncUi.ShouldArmListHold(appWasAlreadyOpen);

        /// <inheritdoc />
        public bool ShouldSuppressDeepLinkSyncBanner(bool deepLinkHeld) =>
            ChatListDeepLinkSyncUi.ShouldSuppressSyncBanner(deepLinkHeld);

        /// <inheritdoc />
        public bool ShouldPreferDeepLinkLoadingOverlay(bool deepLinkHeld, bool listAlreadyMaterialized) =>
            ChatListDeepLinkSyncUi.ShouldPreferLoadingOverlay(deepLinkHeld, listAlreadyMaterialized);

        /// <inheritdoc />
        public bool RequiresNamesAppliedVisibleRebuild(string searchQuery) =>
            ChatListNamesAppliedRefresh.RequiresVisibleRebuild(searchQuery);

        /// <inheritdoc />
        public bool RequiresVisibleReorder(string propertyName) =>
            ChatListReorderTrigger.RequiresVisibleReorder(propertyName);

        /// <inheritdoc />
        public bool ShouldHoldChatListRows(
            bool isAwaitingFirstHistorySync,
            bool deepLinkDetailFirst,
            bool listAlreadyMaterialized) =>
            ChatListSyncHold.ShouldHoldRows(
                isAwaitingFirstHistorySync,
                deepLinkDetailFirst,
                listAlreadyMaterialized);

        /// <inheritdoc />
        public bool MatchesListEnrichmentPhase(string syncStatus) =>
            ListEnrichmentPhase.Matches(syncStatus);
    }
}
