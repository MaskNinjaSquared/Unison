using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Exceptions;
using Unison.Core.Factories;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        public ChatMessageViewModel FindMessageById(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessageViewModel candidate = Messages[i];
                if (candidate != null && string.Equals(candidate.Id, messageId, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads older timeline pages until <paramref name="quotedMessageId"/> appears or history ends.
        /// </summary>
        public async Task<ChatMessageViewModel> NavigateToQuotedMessageAsync(string quotedMessageId, int maxLoadAttempts = 24)
        {
            ChatMessageViewModel target = FindMessageById(quotedMessageId);
            if (target != null)
            {
                return target;
            }

            if (_activeChat == null || string.IsNullOrWhiteSpace(quotedMessageId))
            {
                return null;
            }

            int attempts = 0;
            while (CanLoadMore && attempts < maxLoadAttempts)
            {
                attempts++;
                ChatTimelineLoadMoreResult result = await LoadMoreMessagesAsync().ConfigureAwait(false);
                if (_activeChat == null)
                {
                    return null;
                }

                target = FindMessageById(quotedMessageId);
                if (target != null)
                {
                    return target;
                }

                if (result == null ||
                    result.ReachedStart ||
                    (result.PrependedCount == 0 && !result.WaitingForOnDemand))
                {
                    break;
                }
            }

            return FindMessageById(quotedMessageId);
        }

        /// <summary>
        /// Inserts chronological older rows that are not already on screen. Returns how many
        /// bubble VMs were prepended.
        /// </summary>
        private int PrependOlderPage(IList<ChatMessage> older, string chatJid)
        {
            if (older == null || older.Count == 0)
            {
                return 0;
            }

            RemovePreviewFallbackMessages();
            StampGroupRemoteJid(older, chatJid);

            var existingIds = new HashSet<string>(
                Messages.Where(m => m?.Id != null).Select(m => m.Id),
                StringComparer.Ordinal);

            int prepended = 0;
            for (int i = 0; i < older.Count; i++)
            {
                ChatMessage msg = older[i];
                if (msg == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(msg.Id) && existingIds.Contains(msg.Id))
                {
                    continue;
                }

                InsertTimelineMessage(msg);
                if (!string.IsNullOrEmpty(msg.Id))
                {
                    existingIds.Add(msg.Id);
                }

                prepended++;
            }

            if (prepended > 0)
            {
                TrimTimelineNewestToMax(MaxUiMessageWindow);
            }

            return prepended;
        }

        /// <summary>
        /// Prepends an older page into the UI window (factory VMs). View calls this when scroll is near top
        /// and <see cref="CanLoadMore"/> is true; scroll offset adjustment stays in code-behind.
        /// </summary>
        public async Task<ChatTimelineLoadMoreResult> LoadMoreMessagesAsync()
        {
            var result = new ChatTimelineLoadMoreResult();
            if (!CanLoadMore || _activeChat == null)
            {
                result.ReachedStart = _hasReachedStart;
                return result;
            }

            IsLoadingMore = true;
            try
            {
                string chatJid = _activeChat.JID;
                ChatMessageViewModel oldestVm = Messages.Count > 0 ? Messages[0] : null;
                List<ChatMessage> older = await _messageService.LoadMoreMessagesAsync(
                    chatJid,
                    oldestVm?.Timestamp,
                    oldestVm?.Id);

                if (_activeChat == null ||
                    !string.Equals(_activeChat.JID, chatJid, StringComparison.OrdinalIgnoreCase))
                {
                    return result;
                }

                if (older != null && older.Count > 0)
                {
                    int prepended = PrependOlderPage(older, chatJid);

                    // Self-chat / dual-key overlap: SQLite can return a page already on screen.
                    // Advance past that tip once so scroll-up is not stuck with prepended=0.
                    if (_messageService.IsTimelineDuplicateOnlyPage(older.Count, prepended))
                    {
                        ChatMessage tip = _messageService.OldestTimelinePageTip(older);
                        if (tip != null)
                        {
                            older = await _messageService.LoadMoreMessagesAsync(
                                chatJid,
                                tip.Timestamp,
                                tip.Id);

                            if (_activeChat == null ||
                                !string.Equals(_activeChat.JID, chatJid, StringComparison.OrdinalIgnoreCase))
                            {
                                return result;
                            }

                            if (older != null && older.Count > 0)
                            {
                                prepended = PrependOlderPage(older, chatJid);
                            }
                        }
                    }

                    if (prepended > 0)
                    {
                        _emptyLoadAttempts = 0;
                        result.PrependedCount = prepended;
                        OnPropertyChanged(nameof(CanLoadMore));
                        return result;
                    }
                }

                bool requestedOnDemand =
                    await _messageService.EnsureHistoryOnDemandAsync(chatJid, InitialUiMessageWindow);
                bool pendingOnDemand = _messageService.IsHistoryOnDemandPending(chatJid);

                if (requestedOnDemand || pendingOnDemand)
                {
                    _emptyLoadAttempts = 0;
                    _hasReachedStart = false;
                    result.WaitingForOnDemand = true;
                }
                else
                {
                    _emptyLoadAttempts++;
                    _hasReachedStart = _emptyLoadAttempts >= 2;
                    result.ReachedStart = _hasReachedStart;
                }

                OnPropertyChanged(nameof(CanLoadMore));
                return result;
            }
            finally
            {
                IsLoadingMore = false;
            }
        }

        /// <summary>Newest <paramref name="windowSize"/> rows of a loaded page, in order.</summary>
        public static List<ChatMessage> TakeLastWindow(IList<ChatMessage> source, int windowSize)
        {
            var list = new List<ChatMessage>();
            if (source == null || source.Count == 0 || windowSize <= 0)
            {
                return list;
            }

            int start = Math.Max(0, source.Count - windowSize);
            for (int i = start; i < source.Count; i++)
            {
                if (source[i] != null)
                {
                    list.Add(source[i]);
                }
            }

            return list;
        }

        /// <summary>
        /// Older persisted rows can arrive without the group JID, which is what decides whether a
        /// bubble shows its sender name. Stamps it so group runs render correctly.
        /// </summary>
        public void StampGroupRemoteJid(IList<ChatMessage> messages, string chatJid)
        {
            if (messages == null || string.IsNullOrEmpty(chatJid))
            {
                return;
            }

            bool isGroup = LooksLikeGroupChat(_activeChat) ||
                chatJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            if (!isGroup)
            {
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                var msg = messages[i];
                if (msg == null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(msg.RemoteJid) ||
                    !msg.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
                {
                    msg.RemoteJid = chatJid;
                }
            }
        }

        /// <summary>Drops the ephemeral preview bubbles once real rows are available.</summary>
        public void RemovePreviewFallbackMessages()
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                var model = Messages[i]?.Model;
                if (model == null)
                {
                    continue;
                }

                if (model.IsPreviewFallback ||
                    (_messageService != null && _messageService.IsPreviewFallbackMessageId(model.Id)))
                {
                    RemoveTimelineAt(i);
                }
            }
        }

        /// <summary>
        /// Corrects the chat's list row from the timeline just opened, then asks the store for a
        /// reconcile in case SQLite holds something newer.
        /// </summary>
        public void SyncListStripFromTimeline(
            ChatItem chat,
            string requestedJid,
            IReadOnlyList<ChatMessage> visibleMessages)
        {
            _chatService?.SyncListStripFromTimeline(chat, requestedJid, visibleMessages);
        }

        /// <summary>
        /// Empty timeline while the chat list already shows a preview: surfaces that preview as an
        /// ephemeral bubble and asks the phone for history. Returns the model so the view can run
        /// its group-run layout over it, or null when the chat had nothing to show.
        /// </summary>
        public ChatMessage ApplyPreviewFallback(ChatItem chat, string requestedJid, bool isGroup)
        {
            if (chat == null)
            {
                return null;
            }

            string targetJid = string.IsNullOrWhiteSpace(requestedJid) ? chat.JID : requestedJid;
            string selfName = _strings?.Get("Chat_SelfFallbackName", "You") ?? "You";
            ChatMessage fallback = _messageService?.TryCreatePreviewFallback(chat, selfName);
            if (fallback != null)
            {
                if (isGroup &&
                    (string.IsNullOrEmpty(fallback.RemoteJid) ||
                     !fallback.RemoteJid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)))
                {
                    fallback.RemoteJid = targetJid;
                }

                var vm = CreateMessageVm(fallback);
                if (vm != null)
                {
                    Messages.Add(vm);
                }
            }

            // Ask for history either way — an empty timeline is the reason we are here.
            _ = _messageService.EnsureHistoryOnDemandAsync(targetJid, InitialUiMessageWindow);
            return fallback;
        }

        /// <summary>
        /// Merges a freshly loaded page into the open timeline: strips preview bubbles, refreshes
        /// rows we already show, inserts what is new in order. Only a reader at the end gets the
        /// oldest rows trimmed; <paramref name="followingEnd"/> false leaves every row under the user.
        /// Returns true when the collection changed, so the view knows to redo runs / scroll.
        /// </summary>
        public bool MergeTimelineFromService(IList<ChatMessage> serviceMessages, string requestedJid, bool followingEnd = true)
        {
            if (serviceMessages == null || serviceMessages.Count == 0)
            {
                return false;
            }

            ChatTimelineMergeMode mode = _messageService.ChooseTimelineMergeMode(
                followingEnd,
                IsDetachedFromEnd);
            if (mode == ChatTimelineMergeMode.RejoinEnd)
            {
                RejoinTimelineEnd(serviceMessages, requestedJid);
                return true;
            }

            DateTime? newestShown = Messages.Count > 0 ? Messages[Messages.Count - 1]?.Timestamp : null;
            int countBeforeFallbackStrip = Messages.Count;
            RemovePreviewFallbackMessages();
            bool changed = Messages.Count != countBeforeFallbackStrip;

            var byId = new Dictionary<string, ChatMessageViewModel>(
                Messages.Count,
                StringComparer.Ordinal);
            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessageViewModel row = Messages[i];
                if (row == null || string.IsNullOrWhiteSpace(row.Id))
                {
                    continue;
                }

                byId[row.Id] = row;
            }

            for (int i = 0; i < serviceMessages.Count; i++)
            {
                ChatMessage msg = serviceMessages[i];
                if (msg == null)
                {
                    continue;
                }

                ChatMessageViewModel existing = FindTimelineRow(msg, byId);
                if (existing != null)
                {
                    ApplyLiveFieldsTo(existing.Model, msg);
                    continue;
                }

                if (!_messageService.ShouldInsertLiveTimelineRow(
                        msg.Timestamp,
                        newestShown,
                        IsDetachedFromEnd))
                {
                    continue;
                }

                StampGroupRemoteJid(new[] { msg }, requestedJid);
                ChatMessageViewModel inserted = InsertTimelineMessage(msg);
                if (inserted != null && !string.IsNullOrWhiteSpace(msg.Id))
                {
                    byId[msg.Id] = inserted;
                }

                changed = true;
            }

            if (mode == ChatTimelineMergeMode.FollowEnd)
            {
                TrimTimelineToMax(MaxUiMessageWindow);
            }

            return changed;
        }

        /// <summary>
        /// The window lost its newest rows to load-more, so the recent page cannot be stitched onto
        /// it without a gap. The reader asked for the end: show the latest page instead.
        /// </summary>
        private void RejoinTimelineEnd(IList<ChatMessage> serviceMessages, string requestedJid)
        {
            var latest = serviceMessages.Where(m => m != null).ToList();
            _messageService.SortTimelineInPlace(latest);
            if (latest.Count > InitialUiMessageWindow)
            {
                latest.RemoveRange(0, latest.Count - InitialUiMessageWindow);
            }

            StampGroupRemoteJid(latest, requestedJid);
            ReplaceTimelineWindow(latest);
            _hasReachedStart = false;
            _emptyLoadAttempts = 0;
            OnPropertyChanged(nameof(CanLoadMore));
        }

        /// <summary>
        /// Row already on screen for <paramref name="message"/>. Rows without an id (local echo)
        /// are matched on timestamp + direction + text, which is all they have.
        /// </summary>
        private ChatMessageViewModel FindTimelineRow(
            ChatMessage message,
            Dictionary<string, ChatMessageViewModel> byId)
        {
            if (!string.IsNullOrWhiteSpace(message.Id))
            {
                ChatMessageViewModel row;
                return byId != null && byId.TryGetValue(message.Id, out row) ? row : null;
            }

            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessageViewModel m = Messages[i];
                if (m?.Model == null || !string.IsNullOrWhiteSpace(m.Id))
                {
                    continue;
                }

                if (m.Timestamp == message.Timestamp &&
                    m.IsFromMe == message.IsFromMe &&
                    string.Equals(m.Model.Content, message.Content, StringComparison.Ordinal))
                {
                    return m;
                }
            }

            return null;
        }

        /// <summary>Copies the fields a reload can carry forward onto a row already on screen.</summary>
        private void ApplyLiveFieldsTo(ChatMessage target, ChatMessage source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Status = source.Status;
            target.IsRevoked = source.IsRevoked;
            target.IsPinned = source.IsPinned;
            target.PinnedAtUtc = source.PinnedAtUtc;
            target.PinExpiresAtUtc = source.PinExpiresAtUtc;
            target.RemoteJid = source.RemoteJid;
            target.ParticipantJid = source.ParticipantJid;
            _messageService?.CopyReactionState(target, source);
            if (source.IsRevoked)
            {
                target.Content = source.Content;
                target.Kind = source.Kind;
            }

            if (!string.IsNullOrWhiteSpace(source.QuotedMessageId) ||
                !string.IsNullOrWhiteSpace(source.QuotedText) ||
                source.QuotedKind != ChatPreviewKind.Text)
            {
                target.QuotedMessageId = source.QuotedMessageId;
                target.QuotedText = source.QuotedText;
                target.QuotedKind = source.QuotedKind;
                target.QuotedSenderName = source.QuotedSenderName;
                target.QuotedParticipantJid = source.QuotedParticipantJid;
            }
            if (!string.IsNullOrWhiteSpace(source.ImageUri))
            {
                target.ImageUri = source.ImageUri;
            }

            if (!string.IsNullOrWhiteSpace(source.AudioUri))
            {
                target.AudioUri = source.AudioUri;
            }

            // Document (and kind in general): a sync reload used to leave Kind/Text while the
            // live row already carried Document — the bubble stayed as plain text.
            if (source.Kind != ChatMessageKind.Text || source.IsDocument)
            {
                target.Kind = source.Kind;
            }

            if (source.IsDocument ||
                !string.IsNullOrWhiteSpace(source.DocumentUri) ||
                !string.IsNullOrWhiteSpace(source.DocumentFileName) ||
                !string.IsNullOrWhiteSpace(source.DocumentMediaKeyBase64))
            {
                if (!string.IsNullOrWhiteSpace(source.DocumentUri))
                {
                    target.DocumentUri = source.DocumentUri;
                }

                if (!string.IsNullOrWhiteSpace(source.DocumentFileName))
                {
                    target.DocumentFileName = source.DocumentFileName;
                }

                if (!string.IsNullOrWhiteSpace(source.DocumentMimeType))
                {
                    target.DocumentMimeType = source.DocumentMimeType;
                }

                if (!string.IsNullOrWhiteSpace(source.DocumentMediaKeyBase64))
                {
                    target.DocumentMediaKeyBase64 = source.DocumentMediaKeyBase64;
                }

                if (source.DocumentFileLengthBytes > 0)
                {
                    target.DocumentFileLengthBytes = source.DocumentFileLengthBytes;
                }

                if (target.Kind == ChatMessageKind.Text)
                {
                    target.Kind = ChatMessageKind.Document;
                }

                target.NotifyDocumentDownloadStateChanged();
            }

            target.EnsureKindFromLegacyFlags();
        }

        /// <summary>
        /// Subscribe + wait for presence (or group hint). Raises <see cref="PresenceAnimationRequested"/>.
        /// The WhatsApp Web and Unison shells skip this on Windows Mobile. The legacy shell does not.

        public void ApplyMessageRunLayout(IList<ChatMessage> messages, bool isGroup, ChatItem groupChat)
        {
            if (messages == null || messages.Count == 0)
            {
                return;
            }

            string todayLabel = _strings != null ? _strings.Get("Common_Today", "Today") : "Today";
            string yesterdayLabel = _strings != null
                ? _strings.Get("Common_Yesterday", "Yesterday")
                : "Yesterday";

            MessageRunLayout.Apply(
                messages,
                isGroup,
                groupChat ?? ActiveChat,
                _participants,
                todayLabel,
                yesterdayLabel,
                QuotedMessageIdIsFromMe);

            ChatItem chat = groupChat ?? ActiveChat;
            bool isChannel = _sessionUnreadIsChannel || (chat != null && chat.IsChannel);
            string unreadMessagesFormat = _strings != null
                ? _strings.Get("ChatDetail_UnreadMessagesFormat", "{0} unread messages")
                : "{0} unread messages";
            string unreadUpdatesFormat = _strings != null
                ? _strings.Get("ChatDetail_UnreadUpdatesFormat", "{0} unread updates")
                : "{0} unread updates";

            MessageRunLayout.ApplyUnreadDivider(
                messages,
                _sessionUnreadDividerCount,
                unreadMessagesFormat,
                unreadUpdatesFormat,
                isChannel);

            if (!_inboundHistoryReadCommitted && _chatService != null && chat != null)
            {
                _inboundHistoryReadCommitted = true;
                _ = CommitOpenedInboundReadAsync(chat);
            }

            System.Collections.Generic.IReadOnlyDictionary<string, string> lookup =
                isGroup ? (groupChat ?? ActiveChat)?.MentionLookup : null;
            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessageViewModel row = Messages[i];
                if (row?.Model == null || !row.Model.HasMentions)
                {
                    continue;
                }

                row.RefreshMentions(lookup);
            }
        }

        private async Task CommitOpenedInboundReadAsync(ChatItem chat)
        {
            try
            {
                // Persist only — keep in-memory IsRead/IsUnreadDivider for this open session
                // so the chip and per-bubble markers stay until the next open.
                await _chatService.CommitInboundHistoryReadAsync(chat).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChatDetailViewModel] CommitInboundHistoryRead failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Asks the contact façade for pictures of group authors currently on screen.
        /// Cheap no-op when not a group or the JID list is empty.
        /// </summary>
        public void RequestVisibleParticipantAvatars(IEnumerable<string> participantJids)
        {
            if (_contactService == null || ActiveChat == null || !ActiveChat.IsGroup)
            {
                return;
            }

            if (participantJids == null)
            {
                return;
            }

            var jids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string jid in participantJids)
            {
                if (string.IsNullOrWhiteSpace(jid) || !seen.Add(jid))
                {
                    continue;
                }

                jids.Add(jid);
            }

            if (jids.Count == 0)
            {
                return;
            }

            string groupJid = ActiveChat.JID;
            _ = _contactService.HydrateGroupMemberAvatarsForJidsAsync(groupJid, jids);
        }

        /// <summary>
        /// Relabels date chips after local midnight (Hoje / Ontem / date). Call from the view timer.
        /// Does not re-resolve participant names/avatars — only the date separator fields.
        /// </summary>
        public void RefreshDateSeparators()
        {
            if (Messages.Count == 0)
            {
                return;
            }

            string todayLabel = _strings != null ? _strings.Get("Common_Today", "Today") : "Today";
            string yesterdayLabel = _strings != null
                ? _strings.Get("Common_Yesterday", "Yesterday")
                : "Yesterday";

            var models = new List<ChatMessage>(Messages.Count);
            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessage current = Messages[i]?.Model;
                if (current != null)
                {
                    models.Add(current);
                }
            }

            MessageRunLayout.RefreshDateSeparators(models, todayLabel, yesterdayLabel);
        }

        /// <summary>
        /// Group author photo: roster (live) → cached map → 1:1 index → Person → resolver fallback.
        /// </summary>
        public string ResolveParticipantContactUri(string participantJid, ChatItem groupChat)
        {
            return _participants.ResolveContactUri(participantJid, groupChat ?? ActiveChat);
        }

        /// <summary>
        /// Timeline scan: true when the quoted message id matches an outgoing bubble on screen.
        /// Self-JID checks live in <see cref="IGroupParticipantLookup"/>.
        /// </summary>
        private bool QuotedMessageIdIsFromMe(string quotedId)
        {
            if (string.IsNullOrWhiteSpace(quotedId) || Messages == null || Messages.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < Messages.Count; i++)
            {
                ChatMessage row = Messages[i]?.Model;
                if (row != null &&
                    string.Equals(row.Id, quotedId, StringComparison.Ordinal) &&
                    row.IsFromMe)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
