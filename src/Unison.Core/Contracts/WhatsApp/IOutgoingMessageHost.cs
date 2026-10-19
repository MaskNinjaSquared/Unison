using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>One try at writing a text to the transport.</summary>
    public sealed class OutgoingTextAttempt
    {
        public string ChatJid { get; set; }

        public string Text { get; set; }

        /// <summary>JIDs for @number tokens in <see cref="Text"/>.</summary>
        public IList<string> Mentions { get; set; }

        /// <summary>ContextInfo.nonJidMentions — everyone in the group, not a JID list.</summary>
        public bool MentionAll { get; set; }

        /// <summary>Reply target. Null when this send quotes nothing.</summary>
        public OutgoingQuote Quote { get; set; }

        /// <summary>The same on every attempt, so a retry cannot mint a second bubble.</summary>
        public string MessageId { get; set; }

        /// <summary>1-based. Only for diagnostics; the budgets below already reflect it.</summary>
        public int Attempt { get; set; }

        /// <summary>How long to wait for a reconnect before giving up on this attempt.</summary>
        public TimeSpan ConnectTimeout { get; set; }

        /// <summary>How long the write itself may take before it counts as a dead pipe.</summary>
        public TimeSpan WriteTimeout { get; set; }
    }

    /// <summary>What one attempt observed.</summary>
    public sealed class OutgoingSendAttempt
    {
        public OutgoingSendAttemptOutcome Outcome { get; set; }

        /// <summary>Null when the outcome is <see cref="OutgoingSendAttemptOutcome.Accepted"/>.</summary>
        public Exception Error { get; set; }

        /// <summary>
        /// The transport this attempt wrote to, handed back to
        /// <see cref="IOutgoingMessageHost.InvalidateTransport"/>. A reconnect may have replaced
        /// it by then, and dropping the newer one would kill a healthy connection.
        /// </summary>
        public object TransportToken { get; set; }
    }

    /// <summary>An audio clip on its way out.</summary>
    public sealed class OutgoingAudio
    {
        public byte[] Bytes { get; set; }

        public string MimeType { get; set; }

        public uint DurationSeconds { get; set; }

        /// <summary>Recorded in the app (push-to-talk) rather than picked as a file.</summary>
        public bool IsVoiceMessage { get; set; }
    }

    /// <summary>A document file on its way out (PDF / office / archive / text).</summary>
    public sealed class OutgoingDocument
    {
        public byte[] Bytes { get; set; }

        public string MimeType { get; set; }

        /// <summary>Name shown on the phone and stored on the bubble.</summary>
        public string FileName { get; set; }

        /// <summary>Optional caption; empty sends a plain DocumentMessage.</summary>
        public string Caption { get; set; }
    }

    /// <summary>
    /// What the send flows need from whoever still owns the timeline caches and the socket.
    /// </summary>
    /// <remarks>
    /// Transitional: the client implements it because the in-memory timeline, the outbox and
    /// the socket lifecycle are still its own. The flow that decides what happens in what
    /// order lives in <see cref="OutgoingTextSend"/> and <see cref="OutgoingMediaSend"/> instead.
    /// </remarks>
    public interface IOutgoingMessageHost
    {
        /// <summary>Reconnects with the default budget when the socket is down.</summary>
        Task EnsureConnectedAsync();

        /// <summary>Uploads and sends under <paramref name="messageId"/>; throws when the send fails.</summary>
        Task SendImageAsync(string jid, byte[] imageBytes, string caption, string messageId);

        /// <summary>Uploads and sends under <paramref name="messageId"/>; throws when the send fails.</summary>
        Task SendAudioAsync(string jid, OutgoingAudio audio, string messageId);

        /// <summary>Uploads and sends under <paramref name="messageId"/>; throws when the send fails.</summary>
        Task SendDocumentAsync(string jid, OutgoingDocument document, string messageId);

        /// <summary>Files the bubble in memory: pending receipts applied, inserted in order, id indexed.</summary>
        void AcceptOutgoing(string chatJid, ChatMessage message);

        /// <summary>Moves the chat row to the top with this message as its preview.</summary>
        Task ShowLocalSendPreviewAsync(string chatJid, ChatMessage message, ChatPreviewKind kind);

        void NotifyChatChanged(string chatJid);

        /// <summary>Writes the message to the durable outbox now. Throws when the store does.</summary>
        Task PersistLiveAsync(string chatJid, ChatMessage message);

        /// <summary>Queues the message for the batched chat-file flush and schedules it.</summary>
        void QueueForPersist(string chatJid, ChatMessage message);

        /// <summary>
        /// Reconnects if needed and writes once. Never throws: a failure comes back classified
        /// on the result.
        /// </summary>
        Task<OutgoingSendAttempt> TrySendTextOnceAsync(OutgoingTextAttempt request);

        void InvalidateTransport(OutgoingSendAttempt attempt, string reason);

        void RecordSendAttempt(string messageId);

        void RecordSendSuccess(string messageId);

        void RecordSendFailure(string messageId, Exception error);

        /// <summary>Surfaces the failure to whoever listens for client errors.</summary>
        void ReportError(Exception error);

        /// <summary>
        /// RAM overlay for an open chat: rebuilds the in-memory window, applies held receipts
        /// and queues dirty rows for persist. SQLite history is merged by the message façade.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadLiveOverlayAsync(string chatJid);
    }
}
