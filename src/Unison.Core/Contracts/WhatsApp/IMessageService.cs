using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Helpers;
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
        /// Host / media path: raise <see cref="ChatMessagesChanged"/> (UI thread already
        /// marshalled by the caller when needed).
        /// </summary>
        void NotifyChatMessagesChanged(string chatJid);

        /// <summary>
        /// Host socket path: raise <see cref="PresenceUpdated"/> for a presence / chatstate
        /// update from the live transport.
        /// </summary>
        void NotifyPresenceUpdated(PresenceUpdateEventArgs e);

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
        /// Lightweight reload for <see cref="ChatMessagesChanged"/>: live RAM plus a small SQLite
        /// tail (no pinned/pending extras). Prefer this over <see cref="LoadMessagesForChatAsync"/>
        /// once the chat is already open.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadRecentMessagesForSyncAsync(string jid);

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

        /// <summary>True when the row belongs on the chat-info Media pivot (not stickers).</summary>
        bool IsChatInfoMedia(ChatMessage message);

        /// <summary>True when the row belongs on the chat-info Files pivot.</summary>
        bool IsChatInfoDocument(ChatMessage message);

        /// <summary>Copies reaction aggregates from a reloaded row onto one already on screen.</summary>
        void CopyReactionState(ChatMessage target, ChatMessage source);

        /// <summary>
        /// Ephemeral bubble from list-preview fields when history has not arrived yet.
        /// Never persist these rows.
        /// </summary>
        ChatMessage TryCreatePreviewFallback(ChatItem chat, string selfDisplayName = null);

        /// <summary>True when <paramref name="id"/> was minted by <see cref="TryCreatePreviewFallback"/>.</summary>
        bool IsPreviewFallbackMessageId(string id);

        /// <summary>
        /// The @ at <paramref name="caret"/> when it starts a mention token (groups composer).
        /// </summary>
        bool TryReadMentionToken(string text, int caret, out int at, out string query, out int length);

        /// <summary>@ menu rows: Everyone on top, then roster matches.</summary>
        IReadOnlyList<MentionChoice> SearchMentionChoices(
            IEnumerable<GroupMember> members,
            string query,
            string everyoneLabel,
            string everyoneDetail,
            string everyoneAvatarUrl,
            Func<GroupMember, bool> exclude = null);

        /// <summary>
        /// True when a load-more page returned rows already on screen (cursor overlap).
        /// </summary>
        bool IsTimelineDuplicateOnlyPage(int returnedCount, int prependedCount);

        /// <summary>Oldest row of a chronological page — cursor for the next load-more.</summary>
        ChatMessage OldestTimelinePageTip(IList<ChatMessage> chronologicalPage);

        /// <summary>
        /// How a live recent-page reload lands on the open timeline (follow end / rejoin / hold scroll).
        /// </summary>
        ChatTimelineMergeMode ChooseTimelineMergeMode(bool followingEnd, bool isDetachedFromEnd);

        /// <summary>
        /// Whether a live row may insert while the window may be detached from the newest messages.
        /// </summary>
        bool ShouldInsertLiveTimelineRow(DateTime timestamp, DateTime? newestShown, bool isDetachedFromEnd);

        /// <summary>
        /// Protocol timestamp then id — same order the message store persists. Mutates <paramref name="list"/>.
        /// </summary>
        void SortTimelineInPlace(List<ChatMessage> list);

        /// <summary>
        /// Index for inserting a row into an already-ordered timeline window (timestamp, then id).
        /// </summary>
        int FindTimelineInsertIndex(
            int count,
            Func<int, DateTime> getTimestamp,
            Func<int, string> getId,
            DateTime timestamp,
            string id);

        /// <summary>Asks the phone for older messages for an open chat.</summary>
        Task<bool> EnsureHistoryOnDemandAsync(string jid, int count);

        /// <summary>True while an on-demand history request is in flight for <paramref name="jid"/>.</summary>
        bool IsHistoryOnDemandPending(string jid);

        Task<ChatMessage> SendTextMessageAsync(string jid, string text);

        /// <summary>Same send, with @mentions already turned into JIDs and the everyone flag.</summary>
        Task<ChatMessage> SendTextMessageAsync(string jid, OutgoingTextMentions message);

        Task SendImageAsync(string jid, byte[] imageBytes, string caption);

        /// <summary>
        /// Sends selected images in order. The caption belongs to the first image only.
        /// Each image is independently persisted and can settle as sent or failed.
        /// </summary>
        Task<IReadOnlyList<ChatMessage>> SendImagesAsync(
            string jid,
            IReadOnlyList<byte[]> imageBytes,
            string caption);

        /// <summary>Sends a recorded/picked audio clip (voice note when isVoiceMessage=true).</summary>
        Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false);

        /// <summary>Sends a picked document (PDF / office / archive / text). MIME must be document-only.</summary>
        Task<ChatMessage> SendDocumentAsync(string jid, byte[] documentBytes, string mimeType, string fileName, string caption = null);

        /// <summary>Downloads + decrypts the audio media on demand, caching the local URI on the message.</summary>
        Task<string> EnsureAudioAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts an image on demand, caching the local URI on the message.</summary>
        Task<string> EnsureImageAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a video or GIF on demand, caching the local URI (+ poster) on the message.</summary>
        Task<string> EnsureVideoAvailableAsync(ChatMessage message);

        /// <summary>Downloads + decrypts a document on demand, caching the local URI on the message.</summary>
        Task<string> EnsureDocumentAvailableAsync(ChatMessage message);

        /// <summary>Downloads an image that just arrived, beside the incoming pump.</summary>
        Task HydrateIncomingImageAsync(
            ChatMessage message,
            Message.Types.ImageMessage image,
            string messageId,
            string chatJid);

        /// <summary>Downloads a sticker that just arrived, beside the incoming pump.</summary>
        Task HydrateIncomingStickerAsync(
            ChatMessage message,
            Message.Types.StickerMessage sticker,
            string messageId,
            string chatJid);

        /// <summary>
        /// Fire-and-forget sticker/image hydrate for a live insert. Failures are logged; the
        /// pump only builds the plan and calls this.
        /// </summary>
        void ScheduleIncomingMediaHydration(
            ChatMessage message,
            Message.Types.StickerMessage sticker,
            Message.Types.ImageMessage image,
            string messageId,
            string chatJid);

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
        /// Live inbound announce: toast / badge / tile. From-me is a no-op. Mute is read from
        /// <paramref name="chat"/> (after local store overlay) or the chat-store cache.
        /// </summary>
        void NotifyLiveIncoming(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isFromMe,
            bool suppressToast,
            int totalUnread,
            ChatItem chat);

        /// <summary>
        /// Inbound revoke: tombstone the target (never insert a synthetic deleted row),
        /// drop a leftover envelope row from older builds, persist, notify the open chat,
        /// and refresh the list tip when the revoked message was the preview.
        /// </summary>
        Task ApplyIncomingRevocationAsync(
            string chatJid,
            string targetMessageId,
            string envelopeMessageId = null);

        /// <summary>
        /// Inbound pin-in-chat / unpin: computes the pin window then applies
        /// <see cref="ApplyPinnedMessageStateAsync"/>.
        /// </summary>
        Task ApplyIncomingPinInChatAsync(
            string chatJid,
            string targetMessageId,
            bool pin,
            long senderTimestampMs,
            uint durationSeconds = 0);

        /// <summary>
        /// Account-side delete-for-me: drop the bubble from RAM, refresh alias list tips,
        /// delete the SQLite row, and notify the open chat.
        /// </summary>
        Task<bool> ApplyAppStateDeleteMessageAsync(string chatJid, string messageId);

        /// <summary>
        /// Local message pin / unpin onto the timeline and SQLite (pending hold when the
        /// body is not resident yet). Used by send-pin and inbound pin-in-chat.
        /// </summary>
        Task<bool> ApplyPinnedMessageStateAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc);

        /// <summary>
        /// Puts one live/offline <see cref="ChatMessage"/> into the in-memory timeline
        /// (alias consolidate, same-chat enrich, or insert+trim). Host still owns alias
        /// consolidate side-effects (SQLite delete / list dedupe) after this returns.
        /// </summary>
        IncomingTimelineAcceptResult AcceptIncomingTimeline(
            string chatJid,
            ChatMessage message,
            bool isGroup);

        /// <summary>
        /// Queues an accepted inbound row for the debounced SQLite batch and schedules the
        /// chat-catalogue persist. Same path as an outgoing <c>QueueForPersist</c>.
        /// </summary>
        void QueueIncomingPersist(string chatJid, ChatMessage message);

        /// <summary>
        /// Offline fast-path: when the id is already in RAM, fill blank participant /
        /// sender name from the envelope without re-running render / UI work. Returns
        /// true when the bubble changed and was re-queued for persist.
        /// </summary>
        bool EnrichOfflineDuplicateFast(
            string chatJid,
            string messageId,
            bool isFromMe,
            string participantJid,
            string verifiedName,
            string pushName);

        /// <summary>
        /// Inbound reaction: map onto the parent bubble (UI-thread safe), persist the parent
        /// when found, and notify the open chat. Returns false when the envelope is not a
        /// reaction this façade handles.
        /// </summary>
        Task<bool> ApplyIncomingReactionAsync(
            Message message,
            ChatMessageMapContext context);

        /// <summary>
        /// After <see cref="AcceptIncomingTimeline"/> returns
        /// <see cref="IncomingTimelineAcceptKind.AliasConsolidated"/>: drop the source-store
        /// row, persist the survivor, and dedupe the chat list.
        /// </summary>
        Task FinalizeAliasConsolidateAsync(
            string canonicalChatJid,
            string aliasSourceChatJid,
            string messageId,
            ChatMessage consolidatedMessage);

        /// <summary>
        /// Whether the in-memory timeline already indexes <paramref name="messageId"/>
        /// under <paramref name="chatJid"/> (and, for DMs, alias buckets).
        /// </summary>
        bool HasCachedMessageId(string chatJid, string messageId, bool isGroup);

        /// <summary>
        /// Outgoing receipt / send-ack status onto resident bubbles (and list tip ticks).
        /// When the id is not in RAM, holds the status until the chat is opened.
        /// </summary>
        Task ApplyOutgoingStatusAsync(string messageId, string status, string error = null);

        /// <summary>
        /// Wire <c>receipt</c> node: direct status or group tally, then
        /// <see cref="ApplyOutgoingStatusAsync"/>.
        /// </summary>
        Task HandleReceiptAsync(Unison.Baileys.Protocol.BinaryNode node);

        /// <summary>
        /// Files an outgoing bubble in the in-memory timeline (held receipt/pin applied first).
        /// </summary>
        void AcceptOutgoingTimeline(string chatJid, ChatMessage message);

        /// <summary>
        /// Applies held receipt/pin state onto a live or replayed bubble. Returns true when
        /// the message changed.
        /// </summary>
        bool ApplyHeldMessageState(string chatJid, ChatMessage message);

        /// <summary>
        /// RAM overlay for an open chat: rebuilds the window, applies held state, queues dirty
        /// rows. SQLite history is merged by <see cref="LoadMessagesForChatAsync"/>.
        /// </summary>
        Task<System.Collections.Generic.List<ChatMessage>> LoadLiveOverlayAsync(string chatJid);

        /// <summary>
        /// After <see cref="AcceptIncomingTimeline"/> returns a duplicate: persist if changed,
        /// resolve missing-message, refresh tip.
        /// </summary>
        Task ApplyIncomingDuplicateAsync(IncomingDuplicateApplyRequest request);

        /// <summary>
        /// After <see cref="AcceptIncomingTimeline"/> consolidates an alias: durable finalize
        /// then tip refresh.
        /// </summary>
        Task ApplyIncomingAliasConsolidatedAsync(IncomingAliasConsolidatedApplyRequest request);

        /// <summary>
        /// After <see cref="AcceptIncomingTimeline"/> inserts: offline tip + persist, or live
        /// list apply + notify + persist, plus media hydrate.
        /// </summary>
        Task ApplyIncomingInsertedAsync(IncomingInsertedApplyRequest request);

        /// <summary>
        /// Offline fast-path: when the id is already in RAM, enrich blank fields, clear
        /// missing-message, and skip the full inbound pipeline. Returns true when handled.
        /// </summary>
        bool TryHandleOfflineDuplicateFast(
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string verifiedName,
            string pushName);

        /// <summary>
        /// Reaction envelope: builds sender naming when needed and applies. Returns true when
        /// the envelope was a reaction this façade handled.
        /// </summary>
        Task<bool> ApplyIncomingReactionFromEnvelopeAsync(
            Message message,
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string selfDisplayName,
            DateTime timestampUtc);

        /// <summary>
        /// After routing: render → push-name → sender/attention → build bubble →
        /// <see cref="AcceptIncomingTimeline"/> → alias/duplicate/insert follow-up.
        /// </summary>
        Task ProcessIncomingChatContentAsync(IncomingChatContentProcessRequest request);

        /// <summary>
        /// Revoke, pin-in-chat, or reaction: returns true when the envelope was one of those
        /// and was applied (caller should stop). False means fall through to content.
        /// </summary>
        Task<bool> TryApplyIncomingEnvelopeControlAsync(
            Message message,
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string selfDisplayName,
            DateTime timestampUtc);

        /// <summary>
        /// After session/placeholder handling: status broadcast, offline fast-path, DM routing,
        /// self-collapse, envelope controls, then <see cref="ProcessIncomingChatContentAsync"/>.
        /// </summary>
        Task RouteIncomingChatEnvelopeAsync(IncomingRoutedEnvelopeRequest request);

        /// <summary>
        /// Placeholder envelope: note the missing id on the host ledger and request a resend.
        /// </summary>
        Task HandleIncomingPlaceholderAsync(
            string chatJid,
            string participantJid,
            string messageId,
            bool isFromMe,
            DateTime timestampUtc,
            string placeholderType);

        /// <summary>
        /// Socket / envelope missing-message: note the id and request a placeholder resend
        /// with the given ledger reason and dispatch trigger.
        /// </summary>
        Task NoteMissingMessageAndRequestResendAsync(
            string chatJid,
            string participantJid,
            string messageId,
            bool isFromMe,
            DateTime timestampUtc,
            string reason,
            string resendTrigger);

        /// <summary>
        /// Wire <c>ack</c> for an outstanding placeholder-resend request.
        /// </summary>
        void HandlePlaceholderResendAck(string ackClass, string ackId, string ackError);

        /// <summary>
        /// Host socket path: one inbound <c>ack</c> node — placeholder resend then history
        /// on-demand (history owns its own filter).
        /// </summary>
        void NotifyInboundWireAck(string ackClass, string ackId, string ackError);

        /// <summary>
        /// Host socket path: outgoing delivery/read status from the live transport.
        /// Faults are logged on the façade (host must not swallow them).
        /// </summary>
        Task NotifyOutgoingStatusAsync(string messageId, string status, string error);

        /// <summary>
        /// Host socket path: receipt node from the live transport. Faults are logged here.
        /// </summary>
        Task NotifyReceiptAsync(Unison.Baileys.Protocol.BinaryNode node);

        /// <summary>
        /// After offline replay (or a follow-up): schedule a deferred pass that asks the phone
        /// for outstanding placeholder resends. Host only decides <em>when</em>; the façade owns
        /// the drain loop.
        /// </summary>
        void SchedulePlaceholderResendDrain(string trigger, int maxRequests = 4);

        /// <summary>
        /// Cold-start: load the pending-incoming journal, queue rows for persist, flush once,
        /// then apply chat-list tips. Host only calls this after auth is known.
        /// </summary>
        Task RecoverPendingIncomingJournalAsync();

        /// <summary>
        /// After offline message flush: flush chat-list tips and optionally schedule placeholder
        /// resend drain. Host keeps SQLite flush + synced / persist enable.
        /// </summary>
        Task NotifyOfflineReplaySettledAsync(
            string reason,
            bool schedulePlaceholderDrain,
            int maxDrainRequests = 8);

        /// <summary>
        /// Pump entry: assemble quiet/visibility/active-chat context and route the envelope.
        /// </summary>
        Task NotifyIncomingDecryptedAsync(IncomingDecryptedNotifyRequest request);

        /// <summary>
        /// Peer-data placeholder-resend payloads: decode WebMessageInfo bytes and re-enqueue
        /// onto the host ingest queue (never recursive into the pump lock).
        /// </summary>
        Task ApplyPeerDataPlaceholderResendsAsync(
            string stanzaId,
            System.Collections.Generic.IEnumerable<Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult> results);

        /// <summary>
        /// Full peer-data operation response: history-on-demand observe logs plus
        /// <see cref="ApplyPeerDataPlaceholderResendsAsync"/>.
        /// </summary>
        Task HandlePeerDataOperationResponseAsync(
            Message.Types.PeerDataOperationRequestResponseMessage response);
    }
}
