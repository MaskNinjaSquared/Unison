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
using Unison.WhatsAppClient.Helpers;
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

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        /// <summary>
        /// Group subjects (one or a listing batch) into the same cache a resolved contact name would.
        /// </summary>
        internal Task ApplyGroupSubjectsAsync(IEnumerable<KeyValuePair<string, string>> subjectsByJid) =>
            ApplyResolvedNameBatchAsync(AppStateResolvedNameEntries.ForGroupSubjects(
                subjectsByJid,
                NormalizeJid,
                GetCanonicalJid,
                SanitizeContactLabel));

        internal Task ApplyAppStateContactNameAsync(string jid, string name) =>
            ApplyResolvedNameBatchAsync(AppStateResolvedNameEntries.ForContact(
                jid,
                name,
                NormalizeJid,
                GetCanonicalJid,
                SanitizeContactLabel));

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

            List<ChatItem> dirty = null;
            int resolved = 0;
            await _ui.RunAsync(() =>
            {
                var rows = AppStateChatRows.GetOrAdd(
                    Chats,
                    GetChatRowsForCanonicalJid(canonical),
                    canonical,
                    jid => ResolveDisplayName(jid),
                    ResolveChatKind,
                    out ChatItem created);
                if (created != null)
                {
                    dirty = new List<ChatItem> { created };
                }

                resolved = AppStateChatMutation.StampUnread(rows, read, ref dirty);
            });

            if (dirty == null || dirty.Count == 0)
            {
                return;
            }

            // Badge + shortcut + slice: same policy as ChatFacade.ClearUnreadForChatAsync.
            // A slice, not a full persist — rewriting every preview plus three JSON maps
            // costs seconds on Mobile eMMC.
            ChatUnreadSideEffects.AfterConversationUnreadChanged(
                ChatUnreadTally.TotalAcrossConversations(Chats, GetCanonicalJid),
                canonical,
                resolved,
                dirty,
                total => NotificationService.Instance.UpdateBadge(total),
                (shortcutJid, unread) =>
                    App.Services?.GetService<IShortcutService>()?.UpdateChatUnread(shortcutJid, unread),
                rows => _ = PersistChatCatalogSliceAsync(rows));
        }

        /// <summary>
        /// Removes a conversation because the account says it is gone - either the phone deleted it
        /// or we did.
        /// </summary>
        /// <remarks>
        /// Dropping the in-memory rows is only the visible half. SQLite is what the list and the
        /// timeline are rebuilt from on the next launch, so a chat cleared from RAM alone comes
        /// straight back; a delete that leaves the tail behind is undone by the next history chunk.
        /// Hence both: a tombstone on the preview and a real delete of the messages.
        /// </remarks>
        Task IAppStateLocalHost.ApplyChatDeletionAsync(string jid) =>
            ApplyAppStateDeleteChatAsync(jid);

        internal async Task ApplyAppStateDeleteChatAsync(string jid)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            List<string> keys = new List<string>(ChatPinStoreKeys.For(canonical, _jids));

            await _ui.RunAsync(() =>
            {
                // Both rows, for the same reason the storage side already expands the key:
                // the conversation is listed under PN and under LID, and removing one left
                // the other behind as an empty row until the next launch.
                foreach (var chat in GetChatRowsForCanonicalJid(canonical))
                {
                    if (chat != null)
                    {
                        Chats.Remove(chat);
                    }
                }

                MessagesByChat.Remove(canonical);
                ForgetMissingMessagesForChat(canonical);
                _historyRequests.ForgetChat(canonical);
                _missing.Cooldowns.Remove(canonical);
            });

            await ForgetChatInStorageAsync(canonical, keys).ConfigureAwait(false);

            ChatUnreadSideEffects.AfterConversationUnreadChanged(
                ChatUnreadTally.TotalAcrossConversations(Chats, GetCanonicalJid),
                canonicalJid: null,
                chatUnread: 0,
                dirtyRows: null,
                total => NotificationService.Instance.UpdateBadge(total),
                updateShortcutUnread: null,
                persistRows: null);
            SchedulePersist();
        }

        /// <summary>
        /// The persistent half of a chat deletion: tombstone the list preview, drop the stored
        /// messages, and take the chat out of the catalog the app rehydrates from.
        /// </summary>
        private async Task ForgetChatInStorageAsync(string canonical, IReadOnlyList<string> keys)
        {
            var jids = new List<string>();
            if (keys != null)
            {
                jids.AddRange(keys);
            }

            if (!jids.Any(k => string.Equals(k, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                jids.Add(canonical);
            }

            if (_chatPreviews != null)
            {
                try
                {
                    await _chatPreviews.MarkDeletedAsync(jids, DateTime.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat preview tombstone failed: " + ex.Message);
                }
            }

            if (_historyMessages != null)
            {
                try
                {
                    await _historyMessages.DeleteForChatKeysAsync(jids).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat message delete failed: " + ex.Message);
                }
            }

            try
            {
                _messageStore?.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Message cache clear failed: " + ex.Message);
            }

            try
            {
                // Snapshot on the UI thread. By here we are several awaits deep on a pool
                // thread, and Chats is bound to the list: enumerating it while the UI adds a
                // row throws, and the catch below would turn that into a silently skipped save.
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() => snapshot = Chats.Where(c => c != null).ToList());
                await PersistChatCatalogAsync(snapshot ?? new List<ChatItem>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat catalog persist failed: " + ex.Message);
            }
        }

        internal async Task<bool> ApplyAppStateDeleteMessageAsync(string jid, string messageId)
        {
            string canonical = GetCanonicalJid(jid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            bool removed = false;
            await _ui.RunAsync(() =>
            {
                if (!MessagesByChat.TryGet(canonical, out var messages) ||
                    messages == null ||
                    !MessagesByChat.RemoveMessage(canonical, messageId))
                {
                    return;
                }

                // Every row sharing this identity, not just the first: a conversation is
                // listed under both PN and LID, and correcting one left the other showing a
                // preview of the message that was just deleted.
                AppStateDeleteMessagePreview.RefreshAliasRows(
                    GetChatRowsForCanonicalJid(canonical),
                    messages,
                    canonical,
                    GetCanonicalJid,
                    LocalizedChatStripLabels.SelfFallbackName(),
                    LocalizedChatStripLabels.Yesterday());

                removed = true;
            });

            if (removed)
            {
                // Dropping the in-memory row is only the visible half, the same way it is for
                // a deleted chat: SQLite is what the timeline is rebuilt from, so a message
                // cleared from RAM alone comes back on the next load of this conversation.
                if (_messageStore != null)
                {
                    try
                    {
                        await _messageStore.DeleteMessageAsync(canonical, messageId);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[WhatsAppService] Deleting {messageId} from storage failed: {ex.Message}");
                    }
                }

                QueueChatMessagesChanged(canonical);
            }

            return removed;
        }

        Task IAppStateLocalHost.ApplyChatPinAsync(string jid, bool pinned)
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
                    Chats,
                    GetChatRowsForCanonicalJid(canonical),
                    canonical,
                    jid => ResolveDisplayName(jid),
                    ResolveChatKind,
                    out _);

                long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                touched = new List<ChatItem>();
                foreach (var chat in rows)
                {
                    AppStateChatMutation.ApplyFlags(chat, change, nowUnixMs);
                    touched.Add(chat);
                }

                ChatDisplayOrder.SortInPlace(Chats);
            });

            if (touched != null && change.TouchesAnything)
            {
                foreach (var chat in touched)
                {
                    try
                    {
                        // Only what the mutation spoke about. A mute arriving for a pinned chat
                        // used to carry the rest of the row with it, and whichever field the row
                        // happened to be stale on was overwritten.
                        if (change.Pinned.HasValue)
                        {
                            // pin_v1 indexes one address; history/list rows often use the other.
                            string alias = null;
                            string normalizedRow = JidHelper.Normalize(chat.JID);
                            if (!string.IsNullOrWhiteSpace(normalizedRow))
                            {
                                JidAlias.TryGetValue(normalizedRow, out alias);
                            }
                            IReadOnlyList<string> pinKeys = ChatPinStoreKeys.Merge(
                                ChatPinStoreKeys.For(chat.JID, GetCanonicalJid(chat.JID), alias),
                                ChatPinStoreKeys.For(jid, GetCanonicalJid(jid), null));
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
                        Debug.WriteLine("[WhatsAppService] ChatStore upsert from app-state failed: " + ex.Message);
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
                    await PersistChatCatalogAsync(touched).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Archive status persist failed: " + ex.Message);
                }
            }

            SchedulePersist();
        }
    }
}
