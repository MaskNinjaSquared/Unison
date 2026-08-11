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
        /// Persists people from the payload (UpsertIfChanged), then applies history sync.
        /// </summary>
        Task SyncMessageHistoryAsync(HistorySync sync);

        Task<ChatMessage> SendTextMessageAsync(string jid, string text);

        Task SendImageAsync(string jid, byte[] imageBytes, string caption);

        /// <summary>Sends a recorded/picked audio clip (voice note when isVoiceMessage=true).</summary>
        Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false);

        /// <summary>Downloads + decrypts the audio media on demand, caching the local URI on the message.</summary>
        Task<string> EnsureAudioAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts an image on demand, caching the local URI on the message.</summary>
        Task<string> EnsureImageAvailableAsync(ChatMessage message);

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

        /// <summary>Business: apply buffered reaction envelopes after a history batch.</summary>
        IList<ChatMessage> ApplyBufferedReactions(
            IList<ChatMessage> chatMessages,
            IEnumerable<PendingReaction> pending);
    }
}
