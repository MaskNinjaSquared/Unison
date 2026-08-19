using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Message send + history sync + domain facade (extracted gradually from WhatsAppService).
    /// Prefer <see cref="GetChatMessage"/> and related APIs here; the WA client stays transport-oriented.
    /// </summary>
    public interface IMessageService
    {
        /// <summary>
        /// The message list of a chat changed - the JID is the chat. Sent, received, edited,
        /// deleted and reacted all arrive the same way, so listeners reload rather than patch.
        /// </summary>
        event System.EventHandler<string> ChatMessagesChanged;

        /// <summary>
        /// Typing / online state for a chat the app subscribed to with
        /// <see cref="SubscribeToPresenceAsync"/>.
        /// </summary>
        event System.EventHandler<PresenceUpdateEventArgs> PresenceUpdated;

        /// <summary>
        /// Asks the server to start reporting presence for a chat. It only reports for chats
        /// that asked, and only for a while, so an open conversation renews this.
        /// </summary>
        Task SubscribeToPresenceAsync(string jid);

        /// <summary>
        /// Persists people from the payload (UpsertIfChanged), then applies history sync.
        /// </summary>
        Task SyncMessageHistoryAsync(HistorySync sync);

        /// <summary>
        /// Loads messages for an open chat: SQLite <c>history_message</c> plus live RAM overlay.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadMessagesForChatAsync(string jid);

        /// <summary>
        /// Older page from SQLite <c>history_message</c> (then prefer <see cref="EnsureHistoryOnDemandAsync"/>).
        /// Pass the oldest visible bubble as the cursor.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadMoreMessagesAsync(
            string jid,
            DateTime? beforeUtc = null,
            string beforeMessageId = null);

        /// <summary>
        /// Media + document rows for the chat-info Media / Files panes, newest first: SQLite
        /// <c>history_message</c> media rows merged with the live/JSON cache. Timeline paging is
        /// separate — this never seeds the timeline cache nor asks the phone for history.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadChatMediaIndexAsync(string jid, int limit = 400);

        /// <summary>Asks the phone for older messages for an open chat.</summary>
        Task<bool> EnsureHistoryOnDemandAsync(string jid, int count);

        /// <summary>True while an on-demand history request is in flight for <paramref name="jid"/>.</summary>
        bool IsHistoryOnDemandPending(string jid);

        Task<ChatMessage> SendTextMessageAsync(string jid, string text);

        Task SendImageAsync(string jid, byte[] imageBytes, string caption);

        /// <summary>Sends a recorded/picked audio clip (voice note when isVoiceMessage=true).</summary>
        Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false);

        /// <summary>Downloads + decrypts the audio media on demand, caching the local URI on the message.</summary>
        Task<string> EnsureAudioAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts an image on demand, caching the local URI on the message.</summary>
        Task<string> EnsureImageAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a video on demand, caching the local URI (+ poster) on the message.</summary>
        Task<string> EnsureVideoAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a document on demand, caching the local URI on the message.</summary>
        Task<string> EnsureDocumentAvailableAsync(ChatMessage message);

        /// <summary>Pins/unpins a message for the given chat (WhatsApp pin duration, default 7 days).</summary>
        Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800);

        /// <summary>
        /// Business: if <paramref name="message"/> is a reaction envelope, maps + applies onto
        /// <paramref name="chatMessages"/>. Returns true when it was a reaction (no timeline row).
        /// <paramref name="updatedParent"/> is set when the parent was found and changed.
        /// </summary>
        bool TryHandleReaction(
            Message message,
            ChatMessageMapContext context,
            IList<ChatMessage> chatMessages,
            out ChatMessage updatedParent);

        /// <summary>
        /// Business: maps a reaction envelope to <see cref="PendingReaction"/> without applying
        /// (history buffer — apply later via <see cref="ApplyBufferedReactions"/>).
        /// </summary>
        bool TryBufferReaction(
            Message message,
            ChatMessageMapContext context,
            out PendingReaction pending);

        /// <summary>
        /// Facade: builds a domain <see cref="ChatMessage"/> from transport facts
        /// (context + content snapshot). Prefer this over constructing models in the WA client;
        /// over time more message APIs should land here.
        /// </summary>
        ChatMessage GetChatMessage(ChatMessageMapContext context, ChatMessageContentSnapshot content);

        /// <summary>Business: attach <see cref="WebMessageInfo"/> inline reactions onto a parent message.</summary>
        void AttachHistoryReactions(
            ChatMessage parent,
            IEnumerable<Reaction> reactions,
            ChatMessageMapContext parentContext);

        /// <summary>
        /// Business: apply buffered reaction envelopes after a history batch.
        /// </summary>
        IList<ChatMessage> ApplyBufferedReactions(
            IList<ChatMessage> chatMessages,
            IEnumerable<PendingReaction> pending);

        /// <summary>
        /// User action: wipe local chats/messages (auth stays) and re-pull history
        /// (FULL_HISTORY / reconnect). Prefer this over calling WhatsAppService from VMs.
        /// </summary>
        Task ResyncConversationsAsync(System.IProgress<ConversationResyncPhase> progress = null);

        /// <summary>
        /// User action: ensure a chat row exists for <paramref name="jid"/> (new-chat flow).
        /// </summary>
        void StartNewChat(string jid);
    }
}
