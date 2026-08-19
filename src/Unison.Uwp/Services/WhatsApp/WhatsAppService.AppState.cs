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

        internal void SchedulePersistForAppState(string reason)
        {
            EnableScheduledPersist(reason);
            SchedulePersist();
        }

        /// <summary>
        /// A group was created or renamed while we were connected. The subject is the group's
        /// only name, so it goes in the same cache a resolved contact name would.
        /// </summary>
        internal Task ApplyGroupSubjectAsync(string jid, string subject)
        {
            return ApplyGroupSubjectsAsync(new[] { new KeyValuePair<string, string>(jid, subject) });
        }

        /// <summary>
        /// The same for a batch, which is what a group listing produces.
        /// </summary>
        internal Task ApplyGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid)
        {
            var entries = new List<ResolvedNameEntry>();
            if (subjectsByJid != null)
            {
                foreach (var pair in subjectsByJid)
                {
                    AddResolvedName(entries, pair.Key, pair.Value, isSubject: true);
                }
            }

            return ApplyResolvedNameBatchAsync(entries);
        }

        internal Task ApplyAppStateContactNameAsync(string jid, string name)
        {
            var entries = new List<ResolvedNameEntry>();
            AddResolvedName(entries, jid, name, isSubject: false);
            return ApplyResolvedNameBatchAsync(entries);
        }

        internal async Task ApplyAppStateSelfPushNameAsync(string name)
        {
            if (_authState?.Me == null)
            {
                return;
            }

            string selfJid = NormalizeJid(_authState.Me.Id);
            string sanitized = SanitizeContactLabel(name, selfJid);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            CaptureSelfPushName(sanitized, SelfPushNameAppStateSource);
            await PersistAuthStateAsync(null, "apply-self-contact-name");
            await ApplyAppStateContactNameAsync(selfJid, sanitized);
        }

        internal async Task ApplyAppStateReadStateAsync(string jid, bool read)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var rows = GetChatRowsForCanonicalJid(canonical);
                if (rows.Count == 0)
                {
                    var created = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        Kind = ResolveChatKind(canonical)
                    };
                    Chats.Add(created);
                    rows.Add(created);
                }

                int value = read ? 0 : Math.Max(1, rows.Max(c => Math.Max(0, c.UnreadCount)));
                foreach (var row in rows) row.UnreadCount = value;
            });
            NotificationService.Instance.UpdateBadge(GetTotalUnreadCount());
            SchedulePersist();
        }

        internal async Task ApplyAppStateDeleteChatAsync(string jid)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                if (chat != null)
                {
                    Chats.Remove(chat);
                }

                MessagesByChat.Remove(canonical);
                _messageIdIndexByChat.Remove(canonical);
                _pendingMissingMessagesByChat.Remove(canonical);
                _historyOnDemandMarkerByChat.Remove(canonical);
                _historyOnDemandLastRequestIdByChat.Remove(canonical);
                _historyOnDemandAttemptsByChat.Remove(canonical);
                _historyOnDemandRejectedUntilUtcByChat.Remove(canonical);
                _activeChatReconcileCooldownByChat.Remove(canonical);
            });
        }

        internal async Task<bool> ApplyAppStateDeleteMessageAsync(string jid, string messageId)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            bool removed = false;
            await RunOnUiThreadAsync(() =>
            {
                if (!MessagesByChat.TryGetValue(canonical, out var messages) || messages == null || messages.Count == 0)
                {
                    return;
                }

                var message = messages.FirstOrDefault(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                if (message == null)
                {
                    return;
                }

                messages.Remove(message);
                if (_messageIdIndexByChat.TryGetValue(canonical, out var idSet))
                {
                    idSet.Remove(messageId);
                }

                var chat = Chats.FirstOrDefault(c => GetCanonicalJid(c.JID) == canonical);
                    if (chat != null)
                    {
                        var latest = messages.OrderByDescending(m => m?.Timestamp ?? DateTime.MinValue).FirstOrDefault();
                        if (latest != null)
                        {
                            bool isGroup = canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) || chat.IsGroup;
                            ApplyChatPreviewIfNewer(
                                chat,
                                ChatPreviewNormalizer.FormatListPreview(latest, isGroup),
                                latest.Timestamp,
                                true,
                                ChatPreviewNormalizer.InferKindFromMessage(latest),
                                ChatPreviewNormalizer.FormatListAuthorPrefix(latest, isGroup, SelfListDisplayName()),
                                latest.MentionedJids);
                        }
                        else
                        {
                            chat.LastMessage = string.Empty;
                            chat.LastMessageAuthor = string.Empty;
                            chat.LastMessageMentionedJids = null;
                            chat.LastMessageKind = ChatPreviewKind.Text;
                            chat.Timestamp = string.Empty;
                            chat.LastMessageTimestampUtc = null;
                        }
                    }

                removed = true;
            });

            if (removed)
            {
                QueueChatMessagesChanged(canonical);
            }

            return removed;
        }

        public Task ApplyChatPinAsync(string jid, bool pinned)
        {
            return ApplyAppStateChatFlagsAsync(
                jid,
                pinned: pinned,
                pinnedTimestamp: pinned ? (long?)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null);
        }

        internal async Task ApplyAppStateChatFlagsAsync(
            string jid,
            bool? archived = null,
            bool? pinned = null,
            long? muteEndTimestamp = null,
            long? pinnedTimestamp = null,
            bool applyMute = false)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<ChatItem> touched = null;
            await RunOnUiThreadAsync(() =>
            {
                var rows = GetChatRowsForCanonicalJid(canonical);
                if (rows.Count == 0)
                {
                    var created = new ChatItem
                    {
                        JID = canonical,
                        Name = ResolveDisplayName(canonical),
                        Kind = ResolveChatKind(canonical)
                    };
                    Chats.Add(created);
                    rows.Add(created);
                }

                long effectivePinnedTimestamp = pinnedTimestamp ??
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                touched = new List<ChatItem>();
                foreach (var chat in rows)
                {
                    if (archived.HasValue)
                    {
                        chat.IsArchived = archived.Value;
                    }

                    if (pinned.HasValue)
                    {
                        chat.IsChatPinned = pinned.Value;
                        // 0 marks an explicit unpin so PN/LID dedupe cannot resurrect the pin
                        // from an alias row that has not received the same mutation yet.
                        chat.PinnedTimestamp = pinned.Value
                            ? (long?)(pinnedTimestamp ?? chat.PinnedTimestamp ?? effectivePinnedTimestamp)
                            : 0;
                    }

                    if (applyMute)
                    {
                        // null = unmuted; WhatsApp forever may arrive as 0.
                        chat.MutedUntil = muteEndTimestamp;
                    }

                    touched.Add(chat);
                }

                SortChatsForDisplay();
            });

            if (touched != null && _chatStore != null && (pinned.HasValue || applyMute))
            {
                foreach (var chat in touched)
                {
                    try
                    {
                        await _chatStore.UpsertAsync(
                            chat.JID,
                            chat.LocalStatus,
                            chat.IsWidgetPinned,
                            chat.IsChatPinned,
                            chat.MutedUntil).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WhatsAppService] ChatStore upsert from app-state failed: " + ex.Message);
                    }
                }
            }

            SchedulePersist();
        }
    }
}
