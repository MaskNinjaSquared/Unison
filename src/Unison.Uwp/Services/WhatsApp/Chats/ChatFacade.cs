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
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Socket.AppState;
using Unison.Socket.UseCases.Messages;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.Chats
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

        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IWhatsAppService _appState;
        private readonly IChatStore _chatStore;

        internal ChatFacade(
            IWhatsAppSessionProvider sessions,
            IWhatsAppService appState,
            IChatStore chatStore)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException(nameof(sessions));
            }

            if (appState == null)
            {
                throw new ArgumentNullException(nameof(appState));
            }

            _sessions = sessions;
            _appState = appState;
            _chatStore = chatStore;
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
            var canonicalJid = _appState.GetCanonicalJid(chat.JID);
            var wasPinned = chat.IsChatPinned;

            await _appState.ApplyChatPinAsync(canonicalJid, pinned).ConfigureAwait(false);

            var socket = _sessions.Socket;
            if (socket == null)
            {
                Debug.WriteLine("[ChatFacade] Pin kept locally: no socket to tell the account");
                return;
            }

            try
            {
                await socket.SetChatPinnedAsync(canonicalJid, pinned).ConfigureAwait(false);
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
                    "; previousPinned=" + wasPinned +
                    "; socket=" + socket.GetType().FullName;

                Debug.WriteLine(
                    "[ChatFacade] Pin failed; " + details + Environment.NewLine + ex);
                RuntimeDiagnosticsService.Instance.RecordException(
                    "app-state",
                    "chat-pin-patch-failed",
                    ex,
                    details);

                try
                {
                    await _appState.ApplyChatPinAsync(canonicalJid, wasPinned).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    var rollbackDetails = details + "; rollbackPinned=" + wasPinned;
                    Debug.WriteLine(
                        "[ChatFacade] Pin rollback failed; " + rollbackDetails +
                        Environment.NewLine + rollbackEx);
                    RuntimeDiagnosticsService.Instance.RecordException(
                        "app-state",
                        "chat-pin-rollback-failed",
                        rollbackEx,
                        rollbackDetails);
                }

                throw;
            }
        }

        public async Task MarkReadAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            var unread = chat.UnreadCount;
            var jid = _appState.GetCanonicalJid(chat.JID);

            // Cleared first, and unconditionally: the badge is the part the user is looking at, it
            // should not wait for a round trip to disappear, and a PN/LID alias can leave a second
            // row carrying a count this one no longer has.
            await _appState.ClearUnreadForChatAsync(jid).ConfigureAwait(false);

            var socket = _sessions.Socket;
            if (socket == null || unread <= 0)
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
                    await socket.MarkMessagesReadAsync(incoming.Select(ToReceiptTarget)).ConfigureAwait(false);
                }

                await socket.MarkChatReadAsync(jid, recent.Select(m => ToRangeMessage(jid, m))).ConfigureAwait(false);
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

            var jid = _appState.GetCanonicalJid(chat.JID);

            // The range is read before anything is removed: it names the tail the deletion covers,
            // and after the local wipe there is nothing left to describe.
            var range = CollectTail(jid, MaxDeleteRangeMessages);

            var socket = _sessions.Socket;
            if (socket != null && range.Count > 0)
            {
                try
                {
                    await socket
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
                        "; rangeCount=" + range.Count +
                        "; socket=" + socket.GetType().FullName;

                    Debug.WriteLine("[ChatFacade] Delete failed; " + details + Environment.NewLine + ex);
                    RuntimeDiagnosticsService.Instance.RecordException(
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
                    (socket == null ? "no socket" : "no messages to build the range with"));
            }

            await _appState.ApplyChatDeletionAsync(jid).ConfigureAwait(false);
        }

        /// <summary>
        /// The last <paramref name="take"/> messages of the conversation, oldest first. Both
        /// mark-read and delete send a range rather than a single id, and both only need the tail.
        /// </summary>
        private List<ChatMessage> CollectTail(string jid, int take)
        {
            List<ChatMessage> live;
            try
            {
                live = _appState.GetLiveMessages(jid);
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

            return live
                .Where(m => m != null && !string.IsNullOrEmpty(m.Id))
                .Skip(Math.Max(0, live.Count - take))
                .ToList();
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
    }
}
