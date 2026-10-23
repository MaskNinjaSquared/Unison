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

            if (Contacts != null)
            {
                Contacts.CaptureSelfPushName(sanitized, SelfPushNameCapture.AppStateSource);
            }

            await PersistAuthStateAsync(null, "apply-self-contact-name");
            if (Contacts != null)
            {
                await Contacts.ApplyRemoteContactNameAsync(selfJid, sanitized).ConfigureAwait(false);
            }
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

    }
}
