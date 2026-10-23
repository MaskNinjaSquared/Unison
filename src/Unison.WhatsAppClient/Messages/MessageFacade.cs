using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Messages
{
    /// <summary>
    /// Message facade: Person upsert, domain <see cref="ChatMessage"/> construction, then WA transport.
    /// Prefer growing APIs here (e.g. <see cref="GetChatMessage"/>) instead of in WhatsAppService.
    /// </summary>
    public sealed class MessageFacade : IMessageService
    {
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;
        private readonly IChatMessageMapper _chatMessageMapper;
        private readonly IReactionMapper _reactionMapper;
        private readonly IHistoryService _history;
        private readonly IHistoryMessageStore _historyMessageStore;
        private readonly IJidResolver _jids;
        /// <summary>
        /// Concrete store: AcceptIncomingTimeline needs transitional
        /// <see cref="ChatStateStore.MessagesByChat"/> (not on <see cref="IChatStateStore"/>).
        /// </summary>
        private readonly ChatStateStore _chatState;
        private readonly INotificationService _notifications;
        /// <summary>Matches WhatsAppService.MaxActiveChatMessagesInMemory for trim after insert.</summary>
        private const int MaxActiveChatMessagesInMemory = 300;
        private readonly IChatStore _chatStore;
        private readonly OutgoingTextSend _textSend;
        private readonly OutgoingMediaSend _mediaSend;
        private readonly IMediaAvailability _availability;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IMessageWireActions _wire;
        private readonly IOutgoingMessageHost _outgoingHost;
        private readonly IOutgoingReceiptHost _receiptHost;
        private readonly IBoundMutationGate _boundMutations;
        private readonly IStringResources _strings;
        private readonly IPendingMessageFlushHost _pendingFlush;
        private readonly PendingMessageQueue _pendingMessages;
        private readonly IMessageStore _messageStore;
        private readonly ActiveChat _activeChat;
        private readonly ChatRowIndex _chatRows;
        private readonly PendingMessageState _pendingMessageState;
        private readonly IMissingMessageChatHost _missingMessages;
        private readonly IIncomingMessageEnqueueHost _incomingEnqueue;
        private readonly HistoryOnDemandRegistry _historyOnDemand;
        private readonly Func<IChatService> _resolveChats;
        private readonly Func<IContactService> _resolveContacts;
        private readonly Func<IStatusService> _resolveStatus;
        private readonly Func<IConnectionService> _resolveConnection;
        private readonly ReceiptReader _receipts;
        private readonly GroupReceiptTally _groupReceipts = new GroupReceiptTally();
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly int _sqlOpenPageSize;
        private readonly int _sqlLoadMorePageSize;
        private readonly int _thinTimelineThreshold;

        public MessageFacade(
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            IChatMessageMapper chatMessageMapper,
            IReactionMapper reactionMapper,
            IHistoryService history,
            IHistoryMessageStore historyMessageStore,
            IJidResolver jids,
            INotificationService notifications,
            IOutgoingMessageHost outgoingHost,
            IMediaCache mediaCache,
            IMediaAvailability availability,
            IChatCatalogPersistence catalog,
            IMessageWireActions wire,
            IOutgoingReceiptHost receiptHost,
            IBoundMutationGate boundMutations,
            IPendingMessageFlushHost pendingFlush,
            PendingMessageQueue pendingMessages,
            IMessageStore messageStore,
            ActiveChat activeChat,
            ChatStateStore chatState,
            ChatRowIndex chatRows,
            PendingMessageState pendingMessageState,
            IMissingMessageChatHost missingMessages,
            IIncomingMessageEnqueueHost incomingEnqueue,
            HistoryOnDemandRegistry historyOnDemand,
            Func<IChatService> resolveChats,
            Func<IContactService> resolveContacts,
            Func<IStatusService> resolveStatus,
            Func<IConnectionService> resolveConnection,
            IChatStore chatStore = null,
            IStringResources strings = null,
            ISystemInfoProvider systemInfo = null,
            IRuntimeDiagnostics diagnostics = null)
        {
            _diagnostics = diagnostics;
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _chatMessageMapper = chatMessageMapper ?? throw new ArgumentNullException(nameof(chatMessageMapper));
            _reactionMapper = reactionMapper ?? throw new ArgumentNullException(nameof(reactionMapper));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _historyMessageStore = historyMessageStore
                ?? throw new ArgumentNullException(nameof(historyMessageStore));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _availability = availability ?? throw new ArgumentNullException(nameof(availability));
            _chatStore = chatStore;
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _outgoingHost = outgoingHost ?? throw new ArgumentNullException(nameof(outgoingHost));
            _receiptHost = receiptHost ?? throw new ArgumentNullException(nameof(receiptHost));
            _boundMutations = boundMutations ?? throw new ArgumentNullException(nameof(boundMutations));
            _pendingFlush = pendingFlush ?? throw new ArgumentNullException(nameof(pendingFlush));
            _pendingMessages = pendingMessages ?? throw new ArgumentNullException(nameof(pendingMessages));
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _chatRows = chatRows ?? throw new ArgumentNullException(nameof(chatRows));
            _pendingMessageState = pendingMessageState
                ?? throw new ArgumentNullException(nameof(pendingMessageState));
            _missingMessages = missingMessages
                ?? throw new ArgumentNullException(nameof(missingMessages));
            _incomingEnqueue = incomingEnqueue
                ?? throw new ArgumentNullException(nameof(incomingEnqueue));
            _historyOnDemand = historyOnDemand
                ?? throw new ArgumentNullException(nameof(historyOnDemand));
            _resolveChats = resolveChats
                ?? throw new ArgumentNullException(nameof(resolveChats));
            _resolveContacts = resolveContacts
                ?? throw new ArgumentNullException(nameof(resolveContacts));
            _resolveStatus = resolveStatus
                ?? throw new ArgumentNullException(nameof(resolveStatus));
            _resolveConnection = resolveConnection
                ?? throw new ArgumentNullException(nameof(resolveConnection));
            _strings = strings;
            _receipts = new ReceiptReader(jids);

            _textSend = new OutgoingTextSend(_outgoingHost, _jids);
            _mediaSend = new OutgoingMediaSend(
                _outgoingHost,
                _jids,
                mediaCache ?? throw new ArgumentNullException(nameof(mediaCache)));
            // systemInfo kept in the ctor for DI compatibility; page sizes match desktop on all devices.
            _ = systemInfo;

            // Same SQLite page sizes as desktop (Mobile used to use 30/20).
            _sqlOpenPageSize = 50;
            _sqlLoadMorePageSize = 30;
            _thinTimelineThreshold = Math.Max(5, _sqlOpenPageSize - ThinTimelineOnDemandMargin);

            // Presence still originates on the client socket bridge until session events
            // are subscribed here directly.
            _whatsAppService.OnPresenceUpdate += (s, e) => Relay(() => PresenceUpdated?.Invoke(this, e), "PresenceUpdated");
        }

        public event EventHandler<string> ChatMessagesChanged;

        public event EventHandler<PresenceUpdateEventArgs> PresenceUpdated;

        /// <inheritdoc />
        public void NotifyChatMessagesChanged(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return;
            }

            Relay(() => ChatMessagesChanged?.Invoke(this, chatJid), "ChatMessagesChanged");
        }

        /// <inheritdoc />
        public async Task HandleReceiptAsync(Unison.Baileys.Protocol.BinaryNode node)
        {
            ReceiptFacts receipt = _receipts.Read(node);
            if (receipt == null)
            {
                return;
            }

            // A "sender" receipt is our own echo — whole-message status, not per-participant.
            if (!receipt.IsGroup ||
                string.Equals(receipt.Status, ChatMessage.StatusSent, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string id in receipt.MessageIds)
                {
                    await ApplyOutgoingStatusAsync(id, receipt.Status).ConfigureAwait(false);
                }

                return;
            }

            if (string.IsNullOrWhiteSpace(receipt.Participant) ||
                _receiptHost.IsSelfLinked(receipt.Participant))
            {
                return;
            }

            int expectedRecipients = await _receiptHost
                .GetExpectedGroupRecipientCountAsync(receipt.ChatJid)
                .ConfigureAwait(false);
            if (expectedRecipients <= 0)
            {
                return;
            }

            foreach (string id in receipt.MessageIds)
            {
                string aggregateStatus = _groupReceipts.Register(
                    id,
                    receipt.Participant,
                    receipt.Status,
                    expectedRecipients,
                    DateTime.UtcNow);
                if (!string.IsNullOrWhiteSpace(aggregateStatus))
                {
                    await ApplyOutgoingStatusAsync(id, aggregateStatus).ConfigureAwait(false);
                }
            }
        }

        /// <inheritdoc />
        public async Task ApplyOutgoingStatusAsync(string messageId, string status, string error = null)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            var changedChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changedMessages = new List<KeyValuePair<string, ChatMessage>>();

            await _boundMutations.RunAsync(() =>
            {
                changedMessages = _chatState.MessagesByChat.ApplyOutgoingStatus(
                    messageId,
                    status,
                    (chatJid, wireStatus) => SelfChatStatusPolicy.Resolve(
                        wireStatus,
                        _jids.IsSelfLinked(chatJid)));
                for (int i = 0; i < changedMessages.Count; i++)
                {
                    KeyValuePair<string, ChatMessage> change = changedMessages[i];
                    changedChats.Add(change.Key);
                    string canonical = _jids.GetCanonicalJid(JidHelper.Normalize(change.Key));
                    ListPreviewOutgoingSendState.Apply(
                        _chatRows.FindConversation(canonical),
                        change.Value,
                        change.Value.Status);
                }
            }).ConfigureAwait(false);

            if (changedMessages.Count == 0)
            {
                _pendingMessageState.HoldStatus(messageId, status);
            }
            else
            {
                _pendingMessageState.ForgetStatus(messageId);
            }

            for (int i = 0; i < changedMessages.Count; i++)
            {
                QueueIncomingPersist(changedMessages[i].Key, changedMessages[i].Value);
            }

            foreach (string chat in changedChats)
            {
                _outgoingHost.NotifyChatChanged(chat);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                Debug.WriteLine(
                    "[MessageFacade] Outgoing message " + messageId +
                    " status=" + status + ", error=" + error);
            }
        }

        public Task SubscribeToPresenceAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || !_wire.CanSubscribePresence)
            {
                return Task.CompletedTask;
            }

            return _wire.PresenceSubscribeAsync(jid);
        }

        private static void Relay(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] " + name + " handler failed: " + ex.Message);
            }
        }

        public async Task SyncMessageHistoryAsync(HistorySync sync)
        {
            if (sync == null)
            {
                return;
            }

            try
            {
                await UpsertPeopleFromHistoryAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Person upsert from history failed: " + ex.Message);
            }

            HistorySqliteChunkResult chunk = null;
            try
            {
                chunk = await _history.PersistHistorySqliteChunkAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] History SQLite chunk persist failed: " + ex.Message);
            }

            // Open chat detail listens to ChatMessagesChanged and reloads via LoadMessagesForChatAsync.
            if (chunk?.MessageChatJids != null)
            {
                foreach (string jid in chunk.MessageChatJids)
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        continue;
                    }

                    string captured = jid;
                    Relay(() => ChatMessagesChanged?.Invoke(this, captured), "ChatMessagesChanged");
                }
            }
        }

        /// <summary>
        /// A full open page is never "thin": the threshold has to follow <c>_sqlOpenPageSize</c> or the
        /// smaller Mobile page asks the phone for history on every single open.
        /// </summary>
        private const int ThinTimelineOnDemandMargin = 5;
        private const int OnDemandFetchCount = 80;
        /// <summary>Live ChatMessagesChanged only needs the newest tail for MergeTimeline.</summary>
        private const int SqlSyncPageSize = 30;

        public async Task<List<ChatMessage>> LoadMessagesForChatAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return new List<ChatMessage>();
            }

            List<ChatMessage> fromClient = null;
            string clientError = null;
            try
            {
                fromClient = await _outgoingHost.LoadLiveOverlayAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                clientError = ex.GetType().Name;
                Debug.WriteLine("[MessageFacade] Client LoadMessagesForChat failed: " + ex.Message);
            }

            IReadOnlyList<HistoryMessage> fromSql = null;
            string sqlError = null;
            try
            {
                fromSql = await LoadSqlHistoryRowsAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                sqlError = ex.GetType().Name + ": " + ex.Message;
                Debug.WriteLine("[MessageFacade] SQLite history message load failed: " + ex.Message);
            }

            List<ChatMessage> fromSqlMapped = MapHistoryRows(fromSql);
            List<ChatMessage> merged = OpenChatMessageMerge.Merge(fromSqlMapped, fromClient);

            if (merged.Count > 0)
            {
                try
                {
                    // Seed even when rows came only from live/client so on-demand has a cursor.
                    await _chatState.AddMissingMessagesAsync(jid, merged).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MessageFacade] SeedChatMessagesInMemory failed: " + ex.Message);
                }
            }

            // RECENT history often leaves a single listable message — pull older on open.
            bool onDemandPending = IsHistoryOnDemandPending(jid);
            bool isThin = merged.Count > 0 && merged.Count < _thinTimelineThreshold;
            if (isThin && !onDemandPending)
            {
                _ = RequestOlderHistorySafeAsync(jid, merged.Count);
            }

            await WriteChatOpenLoadAsync(
                jid,
                fromClient?.Count ?? 0,
                fromSql?.Count ?? 0,
                merged.Count,
                isThin,
                onDemandPending,
                clientError,
                sqlError).ConfigureAwait(false);

            return merged;
        }

        /// <summary>
        /// One journal line per open. sql=0 with dbRows&gt;0 means the rows are filed under keys
        /// this chat does not resolve to; dbRows=0 means history never reached SQLite.
        /// </summary>
        private async Task WriteChatOpenLoadAsync(
            string jid,
            int ramCount,
            int sqlCount,
            int mergedCount,
            bool isThin,
            bool onDemandPending,
            string clientError,
            string sqlError)
        {
            if (_diagnostics == null)
            {
                return;
            }

            try
            {
                List<string> keys = ResolveChatKeys(jid);
                string dbRows = "-";
                if (sqlCount == 0 && sqlError == null)
                {
                    dbRows = (await _historyMessageStore.CountAsync().ConfigureAwait(false)).ToString();
                }

                _diagnostics.Write(
                    "chat-open",
                    "load",
                    "chat=" + DiagnosticJidMask.Apply(jid) +
                    "; keys=" + string.Join(",", keys.Select(DiagnosticJidMask.Apply)) +
                    "; ram=" + ramCount +
                    "; sql=" + sqlCount +
                    "; merged=" + mergedCount +
                    "; dbRows=" + dbRows +
                    "; thin=" + isThin +
                    "; onDemandPending=" + onDemandPending +
                    "; clientError=" + (clientError ?? "-") +
                    "; sqlError=" + (sqlError ?? "-"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] chat-open diagnostics failed: " + ex.Message);
            }
        }

        private async Task RequestOlderHistorySafeAsync(string jid, int localCount)
        {
            try
            {
                bool requested = await EnsureHistoryOnDemandAsync(jid, OnDemandFetchCount).ConfigureAwait(false);
                Debug.WriteLine(
                    "[MessageFacade] Thin timeline on-demand for " + jid +
                    " local=" + localCount + " requested=" + requested);
                _diagnostics?.Write(
                    "chat-open",
                    "thin-on-demand",
                    "chat=" + DiagnosticJidMask.Apply(jid) +
                    "; local=" + localCount +
                    "; requested=" + requested);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Thin timeline on-demand failed: " + ex.Message);
                _diagnostics?.RecordException("chat-open", "thin-on-demand-failed", ex);
            }
        }

        /// <summary>
        /// Every SQLite key a chat can be stored under: the JID itself, its canonical form, and the
        /// LID/PN alias (both lookup directions). History rows land under whichever the chunk used —
        /// self-chat especially splits across PN and LID.
        /// </summary>
        private List<string> ResolveChatKeys(string jid)
        {
            return new List<string>(ChatPinStoreKeys.For(jid, _jids));
        }

        /// <inheritdoc />
        public bool IsChatInfoMedia(ChatMessage message)
        {
            return ChatMediaFilter.IsMedia(message);
        }

        /// <inheritdoc />
        public bool IsChatInfoDocument(ChatMessage message)
        {
            return ChatMediaFilter.IsDocument(message);
        }

        /// <inheritdoc />
        public void CopyReactionState(ChatMessage target, ChatMessage source)
        {
            HistoryMessageMapper.CopyReactionState(target, source);
        }

        /// <inheritdoc />
        public ChatMessage TryCreatePreviewFallback(ChatItem chat, string selfDisplayName = null)
        {
            return ChatPreviewMessageFactory.TryCreate(chat, selfDisplayName);
        }

        /// <inheritdoc />
        public bool IsPreviewFallbackMessageId(string id)
        {
            return ChatPreviewMessageFactory.IsPreviewFallbackId(id);
        }

        /// <inheritdoc />
        public bool TryReadMentionToken(string text, int caret, out int at, out string query, out int length)
        {
            return MentionSearch.TryReadToken(text, caret, out at, out query, out length);
        }

        /// <inheritdoc />
        public IReadOnlyList<MentionChoice> SearchMentionChoices(
            IEnumerable<GroupMember> members,
            string query,
            string everyoneLabel,
            string everyoneDetail,
            string everyoneAvatarUrl,
            Func<GroupMember, bool> exclude = null)
        {
            return MentionSearch.Search(
                members,
                query,
                everyoneLabel,
                everyoneDetail,
                everyoneAvatarUrl,
                exclude);
        }

        /// <inheritdoc />
        public bool IsTimelineDuplicateOnlyPage(int returnedCount, int prependedCount)
        {
            return TimelineLoadMorePage.IsDuplicateOnlyPage(returnedCount, prependedCount);
        }

        /// <inheritdoc />
        public ChatMessage OldestTimelinePageTip(IList<ChatMessage> chronologicalPage)
        {
            return TimelineLoadMorePage.OldestTip(chronologicalPage);
        }

        /// <inheritdoc />
        public ChatTimelineMergeMode ChooseTimelineMergeMode(bool followingEnd, bool isDetachedFromEnd)
        {
            return ChatTimelineLiveMerge.Choose(followingEnd, isDetachedFromEnd);
        }

        /// <inheritdoc />
        public bool ShouldInsertLiveTimelineRow(
            DateTime timestamp,
            DateTime? newestShown,
            bool isDetachedFromEnd)
        {
            return ChatTimelineLiveMerge.ShouldInsert(timestamp, newestShown, isDetachedFromEnd);
        }

        /// <inheritdoc />
        public void SortTimelineInPlace(List<ChatMessage> list)
        {
            ChatMessageOrder.SortInPlace(list);
        }

        /// <inheritdoc />
        public int FindTimelineInsertIndex(
            int count,
            Func<int, DateTime> getTimestamp,
            Func<int, string> getId,
            DateTime timestamp,
            string id)
        {
            return ChatMessageOrder.FindInsertIndex(count, getTimestamp, getId, timestamp, id);
        }

        public async Task<List<ChatMessage>> LoadChatMediaIndexAsync(string jid, int limit = 400)
        {
            var result = new List<ChatMessage>();
            if (string.IsNullOrWhiteSpace(jid))
            {
                return result;
            }

            int take = limit <= 0 ? 400 : limit;
            var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            var unkeyed = new List<ChatMessage>();

            foreach (string key in ResolveChatKeys(jid))
            {
                IReadOnlyList<HistoryMessage> rows = null;
                try
                {
                    rows = await _historyMessageStore.GetMediaForChatAsync(key, take).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MessageFacade] SQLite media load failed: " + ex.Message);
                }

                if (rows == null)
                {
                    continue;
                }

                foreach (var row in rows)
                {
                    ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                    if (mapped == null || !ChatMediaFilter.IsMediaOrDocument(mapped))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(mapped.Id))
                    {
                        unkeyed.Add(mapped);
                        continue;
                    }

                    if (!byId.ContainsKey(mapped.Id))
                    {
                        byId[mapped.Id] = mapped;
                    }
                }
            }

            // Live / JSON rows win on collision: they can already carry a local file URI.
            List<ChatMessage> cached = null;
            try
            {
                cached = await _outgoingHost.LoadLiveOverlayAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Client media load failed: " + ex.Message);
            }

            if (cached != null)
            {
                foreach (var message in cached)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    message.EnsureKindFromLegacyFlags();
                    if (!ChatMediaFilter.IsMediaOrDocument(message))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(message.Id))
                    {
                        unkeyed.Add(message);
                        continue;
                    }

                    ChatMessage existing;
                    if (byId.TryGetValue(message.Id, out existing))
                    {
                        HistoryMessageMapper.CopyMediaKeysIfMissing(message, existing);
                        HistoryMessageMapper.CopyForwardedIfMissing(message, existing);
                        HistoryMessageMapper.CopyQuotedParticipantIfMissing(message, existing);
                        HistoryMessageMapper.CopyReactionsIfMissing(message, existing);
                    }

                    byId[message.Id] = message;
                }
            }

            return byId.Values
                .Concat(unkeyed)
                .OrderByDescending(m => m.Timestamp)
                .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                .Take(take)
                .ToList();
        }

        private async Task<IReadOnlyList<HistoryMessage>> LoadSqlHistoryRowsAsync(string jid)
        {
            return await LoadSqlHistoryPageAsync(jid, _sqlOpenPageSize, null, null).ConfigureAwait(false);
        }

        /// <summary>
        /// Newest messages for an already-open chat: prefer the in-memory client overlay, and only
        /// hit SQLite for a small tail so ChatMessagesChanged does not re-run a full open query.
        /// </summary>
        public async Task<List<ChatMessage>> LoadRecentMessagesForSyncAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return new List<ChatMessage>();
            }

            List<ChatMessage> fromClient = null;
            try
            {
                fromClient = await _outgoingHost.LoadLiveOverlayAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Client sync load failed: " + ex.Message);
            }

            IReadOnlyList<HistoryMessage> fromSql = null;
            try
            {
                fromSql = await LoadSqlHistoryPageAsync(jid, SqlSyncPageSize, null, null, mergeExtras: false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] SQLite sync load failed: " + ex.Message);
            }

            return OpenChatMessageMerge.Merge(MapHistoryRows(fromSql), fromClient);
        }

        private static List<ChatMessage> MapHistoryRows(IReadOnlyList<HistoryMessage> fromSql)
        {
            if (fromSql == null || fromSql.Count == 0)
            {
                return null;
            }

            var mapped = new List<ChatMessage>(fromSql.Count);
            for (int i = 0; i < fromSql.Count; i++)
            {
                ChatMessage message = HistoryMessageMapper.ToChatMessage(fromSql[i]);
                if (message != null)
                {
                    mapped.Add(message);
                }
            }

            return mapped;
        }

        private async Task<IReadOnlyList<HistoryMessage>> LoadSqlHistoryPageAsync(
            string jid,
            int limit,
            DateTime? beforeUtc,
            string beforeMessageId,
            bool mergeExtras = true)
        {
            List<string> keys = ResolveChatKeys(jid);
            if (keys.Count == 0)
            {
                return Array.Empty<HistoryMessage>();
            }

            IReadOnlyList<HistoryMessage> page =
                await _historyMessageStore.GetForChatKeysAsync(keys, limit, beforeUtc, beforeMessageId)
                    .ConfigureAwait(false);

            if (page == null || page.Count == 0)
            {
                return page ?? Array.Empty<HistoryMessage>();
            }

            var ordered = page as List<HistoryMessage> ?? new List<HistoryMessage>(page);
            if (!mergeExtras || beforeUtc.HasValue)
            {
                return ordered;
            }

            var pageById = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i] != null && !string.IsNullOrWhiteSpace(ordered[i].MessageId))
                {
                    pageById[ordered[i].MessageId] = ordered[i];
                }
            }

            foreach (string key in keys)
            {
                await MergeSqlExtrasAsync(key, pageById, ordered).ConfigureAwait(false);
            }

            return ordered
                .OrderBy(r => r.TimestampUtc ?? DateTime.MinValue)
                .ThenBy(r => r.MessageId, StringComparer.Ordinal)
                .ToList();
        }

        private async Task MergeSqlExtrasAsync(
            string key,
            Dictionary<string, HistoryMessage> byId,
            List<HistoryMessage> ordered)
        {
            IReadOnlyList<HistoryMessage> pinned = null;
            IReadOnlyList<HistoryMessage> pending = null;
            try
            {
                pinned = await _historyMessageStore.GetPinnedForChatAsync(key, 3).ConfigureAwait(false);
                pending = await _historyMessageStore.GetPendingOutgoingAsync(key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] SQLite extras load failed: " + ex.Message);
            }

            void Add(IReadOnlyList<HistoryMessage> extra)
            {
                if (extra == null)
                {
                    return;
                }

                for (int i = 0; i < extra.Count; i++)
                {
                    HistoryMessage row = extra[i];
                    if (row == null || string.IsNullOrWhiteSpace(row.MessageId))
                    {
                        continue;
                    }

                    if (byId.ContainsKey(row.MessageId))
                    {
                        continue;
                    }

                    byId[row.MessageId] = row;
                    ordered.Add(row);
                }
            }

            Add(pinned);
            Add(pending);
        }

        public async Task<List<ChatMessage>> LoadMoreMessagesAsync(
            string jid,
            DateTime? beforeUtc = null,
            string beforeMessageId = null)
        {
            var result = new List<ChatMessage>();
            if (string.IsNullOrWhiteSpace(jid))
            {
                return result;
            }

            IReadOnlyList<HistoryMessage> rows = null;
            try
            {
                rows = await LoadSqlHistoryPageAsync(jid, _sqlLoadMorePageSize, beforeUtc, beforeMessageId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] SQLite load-more failed: " + ex.Message);
            }

            if (rows == null || rows.Count == 0)
            {
                return result;
            }

            foreach (var row in rows)
            {
                ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                if (mapped != null)
                {
                    result.Add(mapped);
                }
            }

            return result;
        }

        public Task<bool> EnsureHistoryOnDemandAsync(string jid, int count)
        {
            return _history.EnsureChatPageAsync(jid, count);
        }

        public bool IsHistoryOnDemandPending(string jid)
        {
            return _history.IsChatPagePending(jid);
        }

        /// <summary>
        /// Shows the bubble at once, then sends with one bounded reconnect. A transport failure
        /// is the returned message's <see cref="ChatMessage.StatusFailed"/>, not an exception.
        /// </summary>
        public Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            return _textSend.SendAsync(jid, text);
        }

        public Task<ChatMessage> SendTextMessageAsync(string jid, OutgoingTextMentions message)
        {
            return _textSend.SendAsync(jid, message);
        }

        public Task SendImageAsync(string jid, byte[] imageBytes, string caption)
        {
            return _mediaSend.SendImageAsync(jid, imageBytes, caption);
        }

        public Task<IReadOnlyList<ChatMessage>> SendImagesAsync(
            string jid,
            IReadOnlyList<byte[]> imageBytes,
            string caption)
        {
            return _mediaSend.SendImagesAsync(jid, imageBytes, caption);
        }

        public async Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false)
        {
            ChatMessage message = await _mediaSend.SendAudioAsync(jid, new OutgoingAudio
            {
                Bytes = audioBytes,
                MimeType = mimeType,
                DurationSeconds = durationSeconds,
                IsVoiceMessage = isVoiceMessage
            });
            _availability.PrepareLocalPlaybackInBackground(message);
            return message;
        }

        public Task<ChatMessage> SendDocumentAsync(
            string jid,
            byte[] documentBytes,
            string mimeType,
            string fileName,
            string caption = null)
        {
            return _mediaSend.SendDocumentAsync(jid, new OutgoingDocument
            {
                Bytes = documentBytes,
                MimeType = mimeType,
                FileName = fileName,
                Caption = caption
            });
        }

        public Task<string> EnsureAudioAvailableAsync(ChatMessage message)
        {
            return _availability.EnsureAudioAsync(message);
        }

        public Task<string> EnsureImageAvailableAsync(ChatMessage message)
        {
            return _availability.EnsureImageAsync(message);
        }

        public Task<string> EnsureVideoAvailableAsync(ChatMessage message)
        {
            return _availability.EnsureVideoAsync(message);
        }

        public Task<string> EnsureDocumentAvailableAsync(ChatMessage message)
        {
            return _availability.EnsureDocumentAsync(message);
        }

        public Task HydrateIncomingImageAsync(
            ChatMessage message,
            Message.Types.ImageMessage image,
            string messageId,
            string chatJid)
        {
            return _availability.HydrateImageAsync(message, image, messageId, chatJid);
        }

        public Task HydrateIncomingStickerAsync(
            ChatMessage message,
            Message.Types.StickerMessage sticker,
            string messageId,
            string chatJid)
        {
            return _availability.HydrateStickerAsync(message, sticker, messageId, chatJid);
        }

        /// <inheritdoc />
        public void ScheduleIncomingMediaHydration(
            ChatMessage message,
            Message.Types.StickerMessage sticker,
            Message.Types.ImageMessage image,
            string messageId,
            string chatJid)
        {
            if (sticker != null)
            {
                ObserveIncomingWork(
                    HydrateIncomingStickerAsync(message, sticker, messageId, chatJid),
                    "hydrate-sticker",
                    messageId);
            }

            if (image != null)
            {
                ObserveIncomingWork(
                    HydrateIncomingImageAsync(message, image, messageId, chatJid),
                    "hydrate-image",
                    messageId);
            }
        }

        private void ObserveIncomingWork(Task work, string label, string messageId)
        {
            if (work == null)
            {
                return;
            }

            _ = ObserveIncomingWorkAsync(work, label, messageId);
        }

        private async Task ObserveIncomingWorkAsync(Task work, string label, string messageId)
        {
            try
            {
                await work.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[MessageFacade] " + label + " failed for " + (messageId ?? "?") + ": " + ex.Message);
                _diagnostics?.RecordException(
                    "messages",
                    label + "-failed",
                    ex,
                    "messageId=" + (messageId ?? string.Empty));
            }
        }

        public async Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            await _resolveConnection().EnsureConnectedAsync().ConfigureAwait(false);

            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                throw new ArgumentException("A valid message is required", nameof(message));
            }

            if (!_wire.CanSendPin)
            {
                throw new InvalidOperationException("Not connected: the message pin was not sent.");
            }

            string canonical = _jids.GetCanonicalJid(chatJid);
            var key = new MessageKey
            {
                RemoteJid = string.IsNullOrWhiteSpace(message.RemoteJid)
                    ? canonical
                    : JidHelper.Normalize(message.RemoteJid),
                FromMe = message.IsFromMe,
                Id = message.Id
            };
            if (!string.IsNullOrWhiteSpace(message.ParticipantJid))
            {
                key.Participant = message.ParticipantJid;
            }

            await _wire.SendPinInChatMessageAsync(canonical, key, pin, durationSeconds);
            MessagePinWindow window = MessagePinWindow.FromLocal(pin, durationSeconds, DateTime.UtcNow);
            await ApplyPinnedMessageStateAsync(
                canonical, message.Id, pin, window.PinnedAtUtc, window.ExpiresAtUtc);
        }

        public bool TryHandleReaction(
            Message message,
            ChatMessageMapContext context,
            IList<ChatMessage> chatMessages,
            out ChatMessage updatedParent)
        {
            updatedParent = null;
            PendingReaction pending;
            if (!_chatMessageMapper.TryMapReaction(message, context, out pending) || pending == null)
            {
                return false;
            }

            if (chatMessages != null)
            {
                _reactionMapper.TryApply(chatMessages, pending, out updatedParent);
            }

            // A chip-summary bubble holds no reactor rows, so the parent upsert that follows cannot
            // store this reaction. Write the single row on its own — it also lands ahead of a parent
            // we have not received yet.
            _ = PersistLiveReactionAsync(context?.ChatJid, pending);
            return true;
        }

        private async Task PersistLiveReactionAsync(string chatJid, PendingReaction pending)
        {
            if (pending == null || string.IsNullOrWhiteSpace(pending.TargetMessageId))
            {
                return;
            }

            string jid = JidHelper.Normalize(
                string.IsNullOrWhiteSpace(chatJid) ? pending.TargetChatJid : chatJid);
            string reactor = JidHelper.Normalize(pending.ReactorJid);
            if (string.IsNullOrWhiteSpace(reactor) && pending.FromMe)
            {
                reactor = "from-me";
            }

            if (string.IsNullOrWhiteSpace(jid) || string.IsNullOrWhiteSpace(reactor))
            {
                return;
            }

            try
            {
                await _historyMessageStore.UpsertReactionsAsync(new[]
                {
                    new HistoryMessageReaction
                    {
                        ChatJid = jid,
                        MessageId = pending.TargetMessageId.Trim(),
                        ReactorJid = reactor,
                        ReactorName = pending.ReactorName,
                        Emoji = pending.Emoji ?? string.Empty,
                        FromMe = pending.FromMe,
                        ReactionMessageId = pending.ReactionMessageId,
                        TimestampUtc = pending.Timestamp == default(DateTime)
                            ? DateTime.UtcNow
                            : pending.Timestamp
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Live reaction persist failed: " + ex.Message);
            }
        }

        public bool TryBufferReaction(
            Message message,
            ChatMessageMapContext context,
            out PendingReaction pending)
        {
            return _chatMessageMapper.TryMapReaction(message, context, out pending);
        }

        public ChatMessage GetChatMessage(ChatMessageMapContext context, ChatMessageContentSnapshot content)
        {
            return _chatMessageMapper.MapIndividual(context, content);
        }

        public void AttachHistoryReactions(
            ChatMessage parent,
            IEnumerable<Reaction> reactions,
            ChatMessageMapContext parentContext)
        {
            if (parent == null || reactions == null || parentContext == null)
            {
                return;
            }

            foreach (var reaction in reactions)
            {
                var pending = _reactionMapper.MapFromHistoryReaction(reaction, parentContext);
                if (pending != null)
                {
                    _reactionMapper.ApplyToMessage(parent, pending);
                }
            }
        }

        public IList<ChatMessage> ApplyBufferedReactions(
            IList<ChatMessage> chatMessages,
            IEnumerable<PendingReaction> pending)
        {
            return _reactionMapper.Apply(chatMessages, pending);
        }

        /// <summary>
        /// Inserts/updates Person rows and merges the same push names into the in-memory map the
        /// list strip reads. UpsertIfChanged skips writes when nothing changed.
        /// </summary>
        private async Task UpsertPeopleFromHistoryAsync(HistorySync sync)
        {
            await _personStore.InitializeAsync().ConfigureAwait(false);

            int writes = 0;
            var pushPairs = new List<KeyValuePair<string, string>>();

            if (sync.Pushnames != null)
            {
                foreach (var pn in sync.Pushnames)
                {
                    if (string.IsNullOrWhiteSpace(pn?.Id) || string.IsNullOrWhiteSpace(pn.Pushname_))
                    {
                        continue;
                    }

                    string jid = JidHelper.Normalize(pn.Id);
                    string name = pn.Pushname_.Trim();
                    string phone = JidHelper.TryPhoneFromJid(jid);
                    if (!string.IsNullOrWhiteSpace(jid))
                    {
                        pushPairs.Add(new KeyValuePair<string, string>(jid, name));
                    }

                    if (await _personStore.UpsertIfChangedAsync(
                        jid,
                        name,
                        null,
                        phone,
                        PersonSource.Observed).ConfigureAwait(false))
                    {
                        writes++;
                    }
                }
            }

            // Same LID↔PN pairs the preview builder uses: store the name under both so
            // ChatAuthorProjection can resolve a group strip that still holds the LID.
            if (sync.PhoneNumberToLidMappings != null)
            {
                foreach (var mapping in sync.PhoneNumberToLidMappings)
                {
                    if (mapping == null ||
                        string.IsNullOrWhiteSpace(mapping.LidJid) ||
                        string.IsNullOrWhiteSpace(mapping.PnJid))
                    {
                        continue;
                    }

                    string lid = JidHelper.Normalize(mapping.LidJid);
                    string pn = JidHelper.Normalize(mapping.PnJid);
                    string name = FindPushName(pushPairs, pn) ?? FindPushName(pushPairs, lid);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(lid))
                    {
                        continue;
                    }

                    if (!pushPairs.Exists(p =>
                            string.Equals(p.Key, lid, StringComparison.OrdinalIgnoreCase)))
                    {
                        pushPairs.Add(new KeyValuePair<string, string>(lid, name));
                    }

                    if (await _personStore.UpsertIfChangedAsync(
                        lid,
                        name,
                        null,
                        JidHelper.TryPhoneFromJid(pn),
                        PersonSource.Observed).ConfigureAwait(false))
                    {
                        writes++;
                    }
                }
            }

            if (sync.Conversations != null)
            {
                foreach (var conv in sync.Conversations)
                {
                    if (string.IsNullOrWhiteSpace(conv?.Id))
                    {
                        continue;
                    }

                    string jid = JidHelper.Normalize(conv.Id);
                    if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                    {
                        continue;
                    }

                    string name = !string.IsNullOrWhiteSpace(conv.Name)
                        ? conv.Name
                        : conv.DisplayName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string phone = JidHelper.TryPhoneFromJid(jid);
                    if (await _personStore.UpsertIfChangedAsync(
                        jid,
                        name,
                        null,
                        phone,
                        PersonSource.DirectChat).ConfigureAwait(false))
                    {
                        writes++;
                    }
                }
            }

            if (pushPairs.Count > 0)
            {
                await _chatState.MergePushNamesAsync(pushPairs).ConfigureAwait(false);
            }

            Debug.WriteLine("[MessageFacade] Person upserts from history: " + writes);
        }

        private static string FindPushName(
            List<KeyValuePair<string, string>> pairs,
            string jid)
        {
            if (pairs == null || string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            for (int i = 0; i < pairs.Count; i++)
            {
                if (string.Equals(pairs[i].Key, jid, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pairs[i].Value))
                {
                    return pairs[i].Value;
                }
            }

            return null;
        }

        public Task ResyncConversationsAsync(System.IProgress<ConversationResyncPhase> progress = null)
        {
            // The facade falls back to the service on its own when the legacy stack is the one
            // connected, so there is no flag to read here.
            return _history.ResyncConversationsAsync(progress);
        }


        public void NotifyLiveIncoming(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isFromMe,
            bool suppressToast,
            int totalUnread,
            ChatItem chat)
        {
            if (!IncomingLiveNotifyGate.ShouldAnnounce(isFromMe))
            {
                return;
            }

            if (chat != null)
            {
                _chatStore?.ApplyTo(chat);
            }

            bool isMuted = chat != null
                ? chat.IsMutedLocally
                : (_chatStore?.TryGetCached(chatJid)?.IsMutedLocally ?? false);

            // Belt with the pump: safe-mode history catch-up must not toast either.
            bool quietForSync = _history != null && _history.IsInitialSyncSafeMode;
            bool hideToast = suppressToast || quietForSync;

            string name = chatName;
            if (string.IsNullOrWhiteSpace(name) && chat != null)
            {
                name = chat.Name;
            }

            _notifications.NotifyIncomingMessage(
                chatJid,
                name,
                senderName,
                preview,
                isGroup,
                isMuted,
                hideToast,
                totalUnread,
                chat != null ? chat.GetAvatarUrl(preferHigh: false) : null,
                chat != null ? Math.Max(0, chat.UnreadCount) : 0);
        }

        /// <inheritdoc />
        public async Task ApplyIncomingRevocationAsync(
            string chatJid,
            string targetMessageId,
            string envelopeMessageId = null)
        {
            string targetId = targetMessageId;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(targetId))
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(chatJid);
            ChatMessageCache cache = _chatState.MessagesByChat;
            IReadOnlyList<string> aliasKeys = GetAliasCacheKeys(canonical);

            // Older builds stored the revoke envelope itself as a fresh
            // "[Message Deleted]" item. Remove that synthetic row when the same
            // event is replayed; the real target below keeps its original timestamp.
            if (!string.IsNullOrWhiteSpace(envelopeMessageId) &&
                !string.Equals(envelopeMessageId, targetId, StringComparison.Ordinal))
            {
                for (int i = 0; i < aliasKeys.Count; i++)
                {
                    cache.RemoveMessage(aliasKeys[i], envelopeMessageId);
                }

                try
                {
                    await _messageStore.DeleteMessageAsync(canonical, envelopeMessageId)
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }

            ChatMessage target;
            AliasLinkedMessageFind.TryFind(cache, aliasKeys, targetId, out _, out target);

            // If the chat is not resident, read the retained local window once. A revoke is
            // an update to an existing message; it must never be inserted as a new "current" item.
            if (target == null)
            {
                target = await FindPersistedMessageAsync(canonical, targetId).ConfigureAwait(false);
            }

            if (target == null)
            {
                return;
            }

            // Tombstone sets IsRevoked (post-1.8.3). That PropertyChanged fans into XAML bindings;
            // apply on the UI thread when a dispatcher is attached so Mobile does not hit 0x8001010E.
            await _boundMutations.RunAsync(
                () => MessageRevocationContent.ApplyTombstone(target)).ConfigureAwait(false);

            try
            {
                await _outgoingHost.PersistLiveAsync(canonical, target).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Failed to persist revoked message: " + ex.Message);
            }

            if (_activeChat.IsOpen(canonical))
            {
                // Host posts to NotifyChatMessagesChanged + chat-state store.
                _outgoingHost.NotifyChatChanged(canonical);
            }

            ChatMessage latest = null;
            List<ChatMessage> tipSource;
            if (cache.TryGet(canonical, out tipSource))
            {
                latest = ChatPreviewTip.PickLatest(tipSource);
            }

            if (latest != null && string.Equals(latest.Id, target.Id, StringComparison.Ordinal))
            {
                await RefreshChatPreviewFromMessageAsync(canonical, target).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public void AcceptOutgoingTimeline(string chatJid, ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || message == null)
            {
                return;
            }

            ApplyHeldMessageState(chatJid, message);
            _chatState.MessagesByChat.Insert(
                chatJid,
                message,
                MaxActiveChatMessagesInMemory,
                DateTime.UtcNow);
        }

        /// <inheritdoc />
        public bool ApplyHeldMessageState(string chatJid, ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                return false;
            }

            string canonical = _jids.GetCanonicalJid(chatJid);
            return _pendingMessageState.ApplyTo(
                message,
                canonical,
                held => SelfChatStatusPolicy.Resolve(held, _jids.IsSelfLinked(chatJid)));
        }

        /// <inheritdoc />
        public Task<List<ChatMessage>> LoadLiveOverlayAsync(string chatJid)
        {
            string normJid = JidHelper.Normalize(chatJid);
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return Task.FromResult(new List<ChatMessage>());
            }

            try
            {
                List<ChatMessage> pending = _pendingMessages.SnapshotFor(
                    normJid,
                    jid => _jids.GetCanonicalJid(JidHelper.Normalize(jid)));
                List<ChatMessage> cache = _chatState.MessagesByChat.RebuildWindow(
                    normJid,
                    pending,
                    MaxActiveChatMessagesInMemory,
                    DateTime.UtcNow);

                var dirty = new List<ChatMessage>();
                for (int i = 0; i < cache.Count; i++)
                {
                    if (ApplyHeldMessageState(normJid, cache[i]))
                    {
                        dirty.Add(cache[i]);
                    }
                }

                for (int i = 0; i < dirty.Count; i++)
                {
                    QueueIncomingPersist(normJid, dirty[i]);
                }

                return Task.FromResult(new List<ChatMessage>(cache));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] LoadLiveOverlay failed: " + ex.Message);
                return Task.FromResult(new List<ChatMessage>());
            }
        }

        /// <inheritdoc />
        public Task ApplyIncomingPinInChatAsync(
            string chatJid,
            string targetMessageId,
            bool pin,
            long senderTimestampMs,
            uint durationSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(targetMessageId))
            {
                return Task.CompletedTask;
            }

            MessagePinWindow window = MessagePinWindow.FromIncoming(
                pin, senderTimestampMs, durationSeconds, DateTime.UtcNow);
            return ApplyPinnedMessageStateAsync(
                chatJid, targetMessageId, pin, window.PinnedAtUtc, window.ExpiresAtUtc);
        }

        /// <inheritdoc />
        public async Task<bool> ApplyAppStateDeleteMessageAsync(string chatJid, string messageId)
        {
            string canonical = _jids.GetCanonicalJid(chatJid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            bool removed = false;
            List<ChatMessage> remaining = null;
            string selfFallback = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";
            string yesterday = _strings != null
                ? _strings.Get("Common_Yesterday", "Yesterday")
                : "Yesterday";

            await _boundMutations.RunAsync(() =>
            {
                ChatMessageCache cache = _chatState.MessagesByChat;
                if (!cache.TryGet(canonical, out remaining) ||
                    remaining == null ||
                    !cache.RemoveMessage(canonical, messageId))
                {
                    return;
                }

                AppStateDeleteMessagePreview.RefreshAliasRows(
                    _chatRows.FindConversation(canonical),
                    remaining,
                    canonical,
                    _jids.GetCanonicalJid,
                    selfFallback,
                    yesterday);
                removed = true;
            }).ConfigureAwait(false);

            if (!removed)
            {
                return false;
            }

            try
            {
                await _messageStore.DeleteMessageAsync(canonical, messageId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    "[MessageFacade] Deleting " + messageId + " from storage failed: " + ex.Message);
            }

            _outgoingHost.NotifyChatChanged(canonical);
            return true;
        }

        /// <inheritdoc />
        public async Task<bool> ApplyPinnedMessageStateAsync(
            string chatJid,
            string messageId,
            bool isPinned,
            DateTime? pinnedAtUtc,
            DateTime? expiresAtUtc)
        {
            string canonical = _jids.GetCanonicalJid(chatJid);
            if (string.IsNullOrWhiteSpace(canonical) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            PendingPin state = _pendingMessageState.HoldPin(
                canonical, messageId, isPinned, pinnedAtUtc, expiresAtUtc, DateTime.UtcNow);

            ChatMessage target = null;
            await _boundMutations.RunAsync(() =>
            {
                if (_chatState.MessagesByChat.TryGet(canonical, out var messages))
                {
                    target = messages?.FirstOrDefault(
                        m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                }

                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                }
            }).ConfigureAwait(false);

            if (target == null)
            {
                target = await FindPersistedMessageAsync(canonical, messageId).ConfigureAwait(false);
                if (target != null)
                {
                    target.IsPinned = state.IsPinned;
                    target.PinnedAtUtc = state.PinnedAtUtc;
                    target.PinExpiresAtUtc = state.ExpiresAtUtc;
                    try
                    {
                        await _outgoingHost.PersistLiveAsync(canonical, target).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[MessageFacade] Persist pinned live failed: " + ex.Message);
                    }

                    if (_activeChat.IsOpen(canonical))
                    {
                        await _boundMutations.RunAsync(() =>
                            _chatState.MessagesByChat.InsertIfAbsent(
                                canonical, target, MaxActiveChatMessagesInMemory, DateTime.UtcNow))
                            .ConfigureAwait(false);
                    }
                }
            }

            if (target != null)
            {
                _pendingMessageState.ForgetPin(canonical, messageId);
                await PersistPinnedMessageToSqliteAsync(canonical, messageId, state, target)
                    .ConfigureAwait(false);
                QueueIncomingPersist(canonical, target);
                _outgoingHost.NotifyChatChanged(canonical);
                return true;
            }

            await PersistPinnedMessageToSqliteAsync(canonical, messageId, state, null)
                .ConfigureAwait(false);
            return false;
        }

        /// <inheritdoc />
        public Task HandleIncomingPlaceholderAsync(
            string chatJid,
            string participantJid,
            string messageId,
            bool isFromMe,
            DateTime timestampUtc,
            string placeholderType)
        {
            return NoteMissingMessageAndRequestResendAsync(
                chatJid,
                participantJid,
                messageId,
                isFromMe,
                timestampUtc,
                "placeholder:" + (placeholderType ?? string.Empty),
                "placeholder-message");
        }

        /// <inheritdoc />
        public Task NoteMissingMessageAndRequestResendAsync(
            string chatJid,
            string participantJid,
            string messageId,
            bool isFromMe,
            DateTime timestampUtc,
            string reason,
            string resendTrigger)
        {
            _missingMessages.RegisterMissingMessage(
                chatJid,
                participantJid,
                messageId,
                isFromMe,
                timestampUtc,
                reason);
            return _missingMessages.RequestPlaceholderResendAsync(
                chatJid,
                messageId,
                resendTrigger ?? "placeholder-message");
        }

        /// <inheritdoc />
        public void HandlePlaceholderResendAck(string ackClass, string ackId, string ackError)
        {
            bool rejected;
            if (!PlaceholderResendAck.TryRead(ackClass, ackId, ackError, out rejected))
            {
                return;
            }

            PlaceholderResendRequestState requestState;
            if (!_missingMessages.TryApplyPlaceholderResendAck(ackId, rejected, out requestState) ||
                requestState == null)
            {
                return;
            }

            if (rejected)
            {
                Debug.WriteLine(
                    "[MessageFacade] placeholder resend ack rejected for " +
                    requestState.MessageId + " in " + requestState.ChatJid +
                    ": stanzaId=" + ackId + ", error=" + ackError);
            }
            else
            {
                Debug.WriteLine(
                    "[MessageFacade] placeholder resend ack accepted for " +
                    requestState.MessageId + " in " + requestState.ChatJid +
                    ": stanzaId=" + ackId);
            }
        }

        /// <inheritdoc />
        public async Task ApplyPeerDataPlaceholderResendsAsync(
            string stanzaId,
            IEnumerable<Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult> results)
        {
            if (results == null)
            {
                return;
            }

            PlaceholderResendRequestState requestState =
                _missingMessages.TakePlaceholderPeerResponse(stanzaId);

            foreach (var result in results)
            {
                var retryResponse = result?.PlaceholderMessageResendResponse;
                if (retryResponse == null ||
                    !retryResponse.HasWebMessageInfoBytes ||
                    retryResponse.WebMessageInfoBytes == null)
                {
                    continue;
                }

                try
                {
                    var webMessage = WebMessageInfo.Parser.ParseFrom(retryResponse.WebMessageInfoBytes);
                    if (webMessage?.Message == null)
                    {
                        continue;
                    }

                    string remoteJid = webMessage.Key?.RemoteJid;
                    if (string.IsNullOrWhiteSpace(remoteJid))
                    {
                        remoteJid = requestState?.ChatJid;
                    }

                    if (string.IsNullOrWhiteSpace(remoteJid) ||
                        string.IsNullOrWhiteSpace(webMessage.Key?.Id))
                    {
                        continue;
                    }

                    await Task.Delay(500).ConfigureAwait(false);

                    DateTime timestampUtc = webMessage.MessageTimestamp > 0
                        ? DateTimeOffset.FromUnixTimeSeconds((long)webMessage.MessageTimestamp).UtcDateTime
                        : DateTime.MinValue;

                    _incomingEnqueue.EnqueueRecoveredDecryptedMessage(
                        remoteJid,
                        WebMessageParticipant.Resolve(webMessage),
                        webMessage.Key.Id,
                        webMessage.Message,
                        timestampUtc,
                        webMessage.Key?.FromMe ?? false,
                        webMessage.PushName);

                    Debug.WriteLine(
                        "[MessageFacade] Queued recovered message " + webMessage.Key.Id +
                        " from placeholder-resend-response");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        "[MessageFacade] Failed to decode placeholder resend response for stanza " +
                        (stanzaId ?? string.Empty) + ": " + ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public async Task HandlePeerDataOperationResponseAsync(
            Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            if (response == null)
            {
                return;
            }

            Debug.WriteLine(
                "[MessageFacade] PeerDataOperationResponse received: stanzaId=" + response.StanzaId +
                ", requestType=" + response.PeerDataOperationRequestType +
                ", resultCount=" + (response.PeerDataOperationResult?.Count ?? 0));

            HistoryOnDemandRequest historyRequestState = _historyOnDemand.Find(response.StanzaId);

            foreach (var result in response.PeerDataOperationResult
                     ?? Enumerable.Empty<Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult>())
            {
                if (result.FullHistorySyncOnDemandRequestResponse != null)
                {
                    Debug.WriteLine(
                        "[MessageFacade] FullHistorySyncOnDemand response observed: stanzaId=" +
                        response.StanzaId +
                        ", responseCode=" + result.FullHistorySyncOnDemandRequestResponse.ResponseCode +
                        ", requestMetadataId=" +
                        result.FullHistorySyncOnDemandRequestResponse.RequestMetadata?.RequestId);
                }

                if (result.HistorySyncChunkRetryResponse != null)
                {
                    Debug.WriteLine(
                        "[MessageFacade] HistorySyncChunkRetry response observed: stanzaId=" +
                        response.StanzaId +
                        ", responseCode=" + result.HistorySyncChunkRetryResponse.ResponseCode +
                        ", canRecover=" + result.HistorySyncChunkRetryResponse.CanRecover +
                        ", requestId=" + result.HistorySyncChunkRetryResponse.RequestId);
                }

                if (result.SyncdSnapshotFatalRecoveryResponse != null)
                {
                    Debug.WriteLine(
                        "[MessageFacade] SyncD fatal recovery response observed: stanzaId=" +
                        response.StanzaId +
                        ", compressed=" + result.SyncdSnapshotFatalRecoveryResponse.IsCompressed +
                        ", bytes=" +
                        (result.SyncdSnapshotFatalRecoveryResponse.CollectionSnapshot?.Length ?? 0));
                }
            }

            await ApplyPeerDataPlaceholderResendsAsync(
                response.StanzaId,
                response.PeerDataOperationResult).ConfigureAwait(false);

            if (historyRequestState != null &&
                (response.PeerDataOperationRequestType ==
                     Message.Types.PeerDataOperationRequestType.FullHistorySyncOnDemand ||
                 response.PeerDataOperationRequestType ==
                     Message.Types.PeerDataOperationRequestType.HistorySyncOnDemand))
            {
                Debug.WriteLine(
                    "[MessageFacade] PeerDataOperationResponse completed without immediate history payload: requestType=" +
                    historyRequestState.TypeName +
                    ", stanzaId=" + response.StanzaId +
                    ", chat=" + (historyRequestState.ChatJid ?? "<full-history>") +
                    ", baseline=" + historyRequestState.BaselineMessageCount +
                    ", trigger=" + (historyRequestState.TriggerReason ?? "unspecified"));
            }
        }

        /// <inheritdoc />
        public async Task RouteIncomingChatEnvelopeAsync(IncomingRoutedEnvelopeRequest request)
        {
            if (request?.Message == null)
            {
                return;
            }

            IContactService contacts = _resolveContacts();
            contacts?.RegisterIncomingEnvelopeAliasHints(
                request.SenderLid,
                request.FromJid,
                request.PeerRecipientPn,
                request.PeerRecipientLid,
                request.RecipientJid,
                request.ParticipantJid,
                request.ParticipantAltJid);

            string normalizedFromJid = JidHelper.Normalize(request.FromJid);
            if (IncomingEnvelopeDisposition.ClassifyAddress(normalizedFromJid) == IncomingEnvelopeKind.StatusBroadcast ||
                IncomingEnvelopeDisposition.ClassifyAddress(request.FromJid) == IncomingEnvelopeKind.StatusBroadcast)
            {
                IStatusService status = _resolveStatus();
                if (status != null)
                {
                    await status.IngestLiveFromDecryptedAsync(new IncomingLiveStatusRequest
                    {
                        ParticipantJid = request.ParticipantJid,
                        SelfJid = _jids.Self != null ? _jids.Self.Id : null,
                        IsFromMe = request.IsFromMe,
                        MessageId = request.MessageId,
                        PushName = request.PushName,
                        Timestamp = request.Timestamp,
                        Message = request.Message
                    }).ConfigureAwait(false);
                }

                return;
            }

            bool isGroup = JidHelper.IsGroupJid(normalizedFromJid);
            if (request.IsOffline && !string.IsNullOrEmpty(request.MessageId))
            {
                string fastJid = _jids.GetCanonicalJid(normalizedFromJid);
                if (TryHandleOfflineDuplicateFast(
                    fastJid,
                    request.MessageId,
                    isGroup,
                    request.IsFromMe,
                    request.ParticipantJid,
                    request.VerifiedName,
                    request.PushName))
                {
                    return;
                }
            }

            string routingReason = isGroup ? "group-from" : null;
            string jid;
            if (isGroup)
            {
                jid = _jids.GetCanonicalJid(normalizedFromJid);
            }
            else
            {
                IncomingLiveDirectChatRoute route = IncomingLiveDirectChatRouting.Resolve(
                    request.IsFromMe,
                    request.FromJid,
                    request.RecipientJid,
                    request.PeerRecipientPn,
                    request.PeerRecipientLid,
                    request.SenderLid,
                    JidHelper.Normalize,
                    _jids.IsSelfLinked,
                    () => _jids.GetCanonicalJid(_jids.Self != null ? _jids.Self.Id : null),
                    _jids.GetCanonicalJid);
                routingReason = route.Reason;
                jid = route.ChatJid;
            }

            if (string.IsNullOrWhiteSpace(jid))
            {
                jid = _jids.GetCanonicalJid(request.FromJid);
                routingReason = routingReason ?? "fallback-from";
            }

            isGroup = JidHelper.IsGroupJid(jid);

            if (!isGroup)
            {
                string normalizedPeerRecipientLid = JidHelper.Normalize(request.PeerRecipientLid);
                if (IncomingSelfChatCollapseGate.ShouldCollapse(
                        routingReason,
                        normalizedPeerRecipientLid,
                        jid))
                {
                    IChatService chats = _resolveChats();
                    if (chats != null)
                    {
                        _ = chats.CollapseTransientDirectChatAsync(
                            normalizedPeerRecipientLid,
                            jid,
                            "live-self-chat-collapse");
                    }
                }
            }

            if (await TryApplyIncomingEnvelopeControlAsync(
                    request.Message,
                    jid,
                    request.MessageId,
                    isGroup,
                    request.IsFromMe,
                    request.ParticipantJid,
                    request.SelfDisplayName,
                    request.Timestamp)
                .ConfigureAwait(false))
            {
                return;
            }

            bool isActive = request.IsActiveChat != null && request.IsActiveChat(jid);
            await ProcessIncomingChatContentAsync(new IncomingChatContentProcessRequest
            {
                Message = request.Message,
                MessageId = request.MessageId,
                ChatJid = jid,
                FromJid = request.FromJid,
                ParticipantJid = request.ParticipantJid,
                ParticipantAltJid = request.ParticipantAltJid,
                VerifiedName = request.VerifiedName,
                PushName = request.PushName,
                Timestamp = request.Timestamp,
                IsOffline = request.IsOffline,
                IsGroup = isGroup,
                EnvelopeIsFromMe = request.IsFromMe,
                SelfDisplayName = request.SelfDisplayName,
                QuietForSync = request.QuietForSync,
                IsActiveChat = isActive,
                IsWindowVisible = request.IsWindowVisible
            }).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<bool> TryApplyIncomingEnvelopeControlAsync(
            Message message,
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string selfDisplayName,
            DateTime timestampUtc)
        {
            if (message == null || string.IsNullOrWhiteSpace(chatJid))
            {
                return false;
            }

            IncomingEnvelopeKind chatControl = IncomingEnvelopeDisposition.ClassifyChatControl(message);
            if (chatControl == IncomingEnvelopeKind.Revoke)
            {
                await ApplyIncomingRevocationAsync(
                        chatJid,
                        message.ProtocolMessage?.Key?.Id,
                        messageId)
                    .ConfigureAwait(false);
                return true;
            }

            if (chatControl == IncomingEnvelopeKind.PinInChat)
            {
                var pinMessage = message.PinInChatMessage;
                string pinTargetId = pinMessage?.Key?.Id;
                bool pin = pinMessage != null &&
                    pinMessage.Type == Message.Types.PinInChatMessage.Types.Type.PinForAll;
                uint duration = message.MessageContextInfo?.MessageAddOnDurationInSecs ?? 0;
                long senderTs = pinMessage?.SenderTimestampMs ?? 0;
                await ApplyIncomingPinInChatAsync(chatJid, pinTargetId, pin, senderTs, duration)
                    .ConfigureAwait(false);
                return true;
            }

            return await ApplyIncomingReactionFromEnvelopeAsync(
                    message,
                    chatJid,
                    messageId,
                    isGroup,
                    isFromMe,
                    participantJid,
                    selfDisplayName,
                    timestampUtc)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ProcessIncomingChatContentAsync(IncomingChatContentProcessRequest request)
        {
            if (request?.Message == null || string.IsNullOrWhiteSpace(request.ChatJid))
            {
                return;
            }

            string jid = request.ChatJid;
            bool isGroup = request.IsGroup;
            string selfFallback = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";

            MessageRenderInfo renderInfo = MessageRenderReader.Read(
                request.Message,
                line => Debug.WriteLine("[MessageFacade] " + line));
            IncomingEmptyContentSkip emptySkip = IncomingEmptyContentSkip.For(
                renderInfo?.Content,
                request.Message.SenderKeyDistributionMessage != null,
                !string.IsNullOrEmpty(request.MessageId));
            if (emptySkip.ShouldSkip)
            {
                if (emptySkip.ClearMissingLedger && !string.IsNullOrEmpty(request.MessageId))
                {
                    _missingMessages.ResolveMissingMessage(
                        jid,
                        request.MessageId,
                        "empty-content");
                }

                return;
            }

            string content = renderInfo.Content;
            string nameFromMsg = request.VerifiedName ?? request.PushName;
            if (!string.IsNullOrEmpty(nameFromMsg))
            {
                IContactService contacts = _resolveContacts();
                contacts?.AcceptIncomingPushName(
                    nameFromMsg,
                    request.EnvelopeIsFromMe,
                    request.ParticipantJid,
                    request.FromJid,
                    request.IsOffline);
            }

            string participantJid = JidHelper.Normalize(request.ParticipantJid);
            string participantAltJid = JidHelper.Normalize(request.ParticipantAltJid);
            string stripParticipantJid = ParticipantStripIdentity.PreferForDisplay(
                participantJid,
                participantAltJid);
            string resolvedParticipantName =
                (!string.IsNullOrEmpty(stripParticipantJid)
                    ? _chatState.ResolveDisplayName(stripParticipantJid)
                    : null)
                ?? (!string.IsNullOrEmpty(participantJid)
                    ? _chatState.ResolveDisplayName(participantJid)
                    : null);
            IncomingSenderResolution sender = IncomingSenderResolver.Resolve(
                isGroup,
                request.EnvelopeIsFromMe,
                request.SelfDisplayName,
                selfFallback,
                resolvedParticipantName,
                _chatState.ResolveDisplayName(jid),
                !string.IsNullOrEmpty(participantJid) || !string.IsNullOrEmpty(stripParticipantJid));
            string senderName = sender.SenderName;
            bool isActuallyFromMe = sender.IsFromMe;

            IncomingAttention attention = IncomingAttention.For(
                isActuallyFromMe,
                request.IsActiveChat,
                request.IsWindowVisible,
                suppressToastForSync: request.QuietForSync);

            string listAuthorPrefix = isGroup
                ? ChatPreviewNormalizer.FormatListAuthorPrefix(
                    new ChatMessage
                    {
                        SenderName = senderName,
                        IsFromMe = isActuallyFromMe,
                        ParticipantJid = participantJid
                    },
                    true,
                    selfFallback,
                    stripParticipantJid)
                : string.Empty;

            QuotedContext quotedContext = QuotedContext.Read(request.Message);
            string quotedSender = QuotedSenderNaming.Resolve(
                quotedContext.QuotedParticipantJid,
                p => _jids.IsSelfLinked(p),
                p => _chatState.ResolveDisplayName(p),
                p => _chatState.ResolveDisplayName(p),
                selfFallback);

            ChatMessage chatMessage = GetChatMessage(
                new ChatMessageMapContext
                {
                    MessageId = request.MessageId,
                    ChatJid = jid,
                    RemoteJid = jid,
                    ParticipantJid = participantJid,
                    SenderName = senderName,
                    IsFromMe = isActuallyFromMe,
                    Timestamp = MessageTimestampValidity.KeepOrDiscard(request.Timestamp, DateTime.UtcNow),
                    Status = isActuallyFromMe
                        ? SelfChatStatusPolicy.Resolve(
                            ChatMessage.StatusSent,
                            _jids.IsSelfLinked(jid))
                        : null
                },
                IncomingChatMessageSnapshot.FromRender(
                    renderInfo,
                    content,
                    quotedContext.IsForwarded,
                    quotedContext.QuotedText,
                    quotedContext.QuotedKind,
                    quotedSender,
                    quotedContext.QuotedParticipantJid,
                    quotedContext.QuotedMessageId,
                    quotedContext.MentionedJids,
                    quotedContext.MentionAll));

            IncomingMediaMetadata.Apply(chatMessage, renderInfo);
            ApplyHeldMessageState(jid, chatMessage);

            ChatPreviewKind previewKind = LiveChatPreviewApplier.ResolveKind(chatMessage, renderInfo);
            IncomingTimelineAcceptResult timeline = AcceptIncomingTimeline(jid, chatMessage, isGroup);

            if (timeline.Kind == IncomingTimelineAcceptKind.AliasConsolidated)
            {
                await ApplyIncomingAliasConsolidatedAsync(new IncomingAliasConsolidatedApplyRequest
                {
                    ChatJid = jid,
                    IncomingMessage = chatMessage,
                    Timeline = timeline,
                    DisplayContent = content,
                    IsOffline = request.IsOffline,
                    IsGroup = isGroup,
                    IsFromMe = isActuallyFromMe,
                    PreviewKind = previewKind,
                    ListAuthorPrefix = listAuthorPrefix,
                    StripParticipantJid = stripParticipantJid,
                    SenderName = senderName
                }).ConfigureAwait(false);
                return;
            }

            if (timeline.Kind == IncomingTimelineAcceptKind.DuplicateSameChat ||
                timeline.Kind == IncomingTimelineAcceptKind.AliasLinkedDuplicate)
            {
                await ApplyIncomingDuplicateAsync(new IncomingDuplicateApplyRequest
                {
                    ChatJid = jid,
                    IncomingMessage = chatMessage,
                    Timeline = timeline,
                    DisplayContent = content,
                    IsOffline = request.IsOffline,
                    IsGroup = isGroup,
                    IsFromMe = isActuallyFromMe,
                    PreviewKind = previewKind,
                    ListAuthorPrefix = listAuthorPrefix,
                    StripParticipantJid = stripParticipantJid,
                    SenderName = senderName
                }).ConfigureAwait(false);
                return;
            }

            IncomingMediaHydrationPlan mediaPlan = IncomingMediaHydrationPlan.From(renderInfo);
            await ApplyIncomingInsertedAsync(new IncomingInsertedApplyRequest
            {
                ChatJid = jid,
                Message = chatMessage,
                DisplayContent = content,
                NotifyContent = content,
                IsOffline = request.IsOffline,
                IsGroup = isGroup,
                IsFromMe = isActuallyFromMe,
                PreviewKind = previewKind,
                ListAuthorPrefix = listAuthorPrefix,
                StripParticipantJid = stripParticipantJid,
                SenderName = senderName,
                CountsAsUnread = attention.CountsAsUnread,
                SuppressToast = attention.SuppressToast,
                Sticker = mediaPlan.HasSticker ? mediaPlan.Sticker : null,
                Image = mediaPlan.HasImage ? mediaPlan.Image : null
            }).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public bool TryHandleOfflineDuplicateFast(
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string verifiedName,
            string pushName)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            if (!HasCachedMessageId(chatJid, messageId, isGroup))
            {
                return false;
            }

            EnrichOfflineDuplicateFast(
                chatJid,
                messageId,
                isFromMe,
                participantJid,
                verifiedName,
                pushName);
            _missingMessages.ResolveMissingMessage(chatJid, messageId, "offline-duplicate-fast");
            return true;
        }

        /// <inheritdoc />
        public Task<bool> ApplyIncomingReactionFromEnvelopeAsync(
            Message message,
            string chatJid,
            string messageId,
            bool isGroup,
            bool isFromMe,
            string participantJid,
            string selfDisplayName,
            DateTime timestampUtc)
        {
            if (message == null || string.IsNullOrWhiteSpace(chatJid))
            {
                return Task.FromResult(false);
            }

            string reactionParticipant = JidHelper.Normalize(participantJid);
            string reactionSenderName;
            if (isFromMe)
            {
                reactionSenderName = !string.IsNullOrWhiteSpace(selfDisplayName)
                    ? selfDisplayName
                    : (_strings != null
                        ? _strings.Get("Chat_SelfFallbackName", "You")
                        : "You");
            }
            else if (isGroup)
            {
                string key = !string.IsNullOrEmpty(reactionParticipant) ? reactionParticipant : chatJid;
                reactionSenderName = _chatState.ResolveDisplayName(key);
            }
            else
            {
                reactionSenderName = _chatState.ResolveDisplayName(chatJid);
            }

            var reactionContext = new ChatMessageMapContext
            {
                MessageId = messageId,
                ChatJid = chatJid,
                RemoteJid = chatJid,
                ParticipantJid = reactionParticipant,
                SenderName = reactionSenderName,
                IsFromMe = isFromMe,
                Timestamp = MessageTimestampValidity.KeepOrDiscard(timestampUtc, DateTime.UtcNow)
            };

            return ApplyIncomingReactionAsync(message, reactionContext);
        }

        /// <inheritdoc />
        public async Task ApplyIncomingAliasConsolidatedAsync(IncomingAliasConsolidatedApplyRequest request)
        {
            if (request == null ||
                request.Timeline == null ||
                string.IsNullOrWhiteSpace(request.ChatJid) ||
                request.IncomingMessage == null)
            {
                return;
            }

            string jid = request.ChatJid;
            IncomingTimelineAcceptResult timeline = request.Timeline;

            Debug.WriteLine(
                "[MessageFacade] Consolidated alias-linked duplicate " + request.IncomingMessage.Id +
                " from " + timeline.AliasSourceChatJid + " into " + jid);

            await FinalizeAliasConsolidateAsync(
                    jid,
                    timeline.AliasSourceChatJid,
                    request.IncomingMessage.Id,
                    timeline.ConsolidatedMessage)
                .ConfigureAwait(false);

            IChatService chats = _resolveChats();
            if (chats != null)
            {
                await chats.ApplyIncomingEnvelopePreviewAsync(new IncomingEnvelopePreviewRequest
                {
                    Jid = jid,
                    Preview = request.DisplayContent,
                    Timestamp = request.IncomingMessage.Timestamp,
                    IsGroup = request.IsGroup,
                    IsFromMe = request.IsFromMe,
                    IsOffline = request.IsOffline,
                    CountUnread = false,
                    MarkDirtyWhenOffline = true,
                    Kind = request.PreviewKind,
                    Status = request.IncomingMessage.Status,
                    AuthorPrefix = request.ListAuthorPrefix,
                    ParticipantJid = request.IsGroup ? request.StripParticipantJid : null,
                    SenderName = request.IsGroup && !request.IsFromMe ? request.SenderName : null
                }).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task ApplyIncomingDuplicateAsync(IncomingDuplicateApplyRequest request)
        {
            if (request == null ||
                request.Timeline == null ||
                string.IsNullOrWhiteSpace(request.ChatJid) ||
                request.IncomingMessage == null)
            {
                return;
            }

            string jid = request.ChatJid;
            IncomingTimelineAcceptResult timeline = request.Timeline;

            if (timeline.ExistingChanged && timeline.Message != null)
            {
                QueueIncomingPersist(jid, timeline.Message);
                _outgoingHost.NotifyChatChanged(jid);
            }

            if (timeline.Kind == IncomingTimelineAcceptKind.AliasLinkedDuplicate)
            {
                Debug.WriteLine(
                    "[MessageFacade] Alias-linked duplicate arrival for " + request.IncomingMessage.Id +
                    ": existingChat=" + timeline.AliasSourceChatJid + ", finalChat=" + jid);
            }

            _missingMessages.ResolveMissingMessage(jid, request.IncomingMessage.Id, "duplicate-arrival");

            IChatService chats = _resolveChats();
            if (chats != null)
            {
                await chats.ApplyIncomingEnvelopePreviewAsync(new IncomingEnvelopePreviewRequest
                {
                    Jid = jid,
                    Preview = request.DisplayContent,
                    Timestamp = request.IncomingMessage.Timestamp,
                    IsGroup = request.IsGroup,
                    IsFromMe = request.IsFromMe,
                    IsOffline = request.IsOffline,
                    CountUnread = false,
                    MarkDirtyWhenOffline = true,
                    Kind = request.PreviewKind,
                    Status = request.IncomingMessage.Status,
                    AuthorPrefix = request.ListAuthorPrefix,
                    ParticipantJid = request.IsGroup ? request.StripParticipantJid : null,
                    SenderName = request.IsGroup && !request.IsFromMe ? request.SenderName : null
                }).ConfigureAwait(false);
            }
        }

        /// <inheritdoc />
        public async Task ApplyIncomingInsertedAsync(IncomingInsertedApplyRequest request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.ChatJid) ||
                request.Message == null)
            {
                return;
            }

            string jid = request.ChatJid;
            ChatMessage chatMessage = request.Message;

            _missingMessages.ResolveMissingMessage(jid, chatMessage.Id, "live-arrival");

            ScheduleIncomingMediaHydration(
                chatMessage,
                request.Sticker,
                request.Image,
                chatMessage.Id,
                jid);

            IChatService chats = _resolveChats();

            if (request.IsOffline)
            {
                if (chats != null)
                {
                    await chats.ApplyIncomingEnvelopePreviewAsync(new IncomingEnvelopePreviewRequest
                    {
                        Jid = jid,
                        Preview = request.DisplayContent,
                        Timestamp = chatMessage.Timestamp,
                        IsGroup = request.IsGroup,
                        IsFromMe = request.IsFromMe,
                        IsOffline = true,
                        CountUnread = true,
                        MarkDirtyWhenOffline = false,
                        Kind = request.PreviewKind,
                        Status = chatMessage.Status,
                        AuthorPrefix = request.ListAuthorPrefix,
                        ParticipantJid = request.IsGroup ? request.StripParticipantJid : null,
                        SenderName = request.IsGroup && !request.IsFromMe ? request.SenderName : null
                    }).ConfigureAwait(false);
                }

                QueueIncomingPersist(jid, chatMessage);

                if (_activeChat.IsOpen(jid))
                {
                    _outgoingHost.NotifyChatChanged(jid);
                }
                else
                {
                    UnloadMessageCacheIfInactive(jid);
                }

                return;
            }

            if (_activeChat.IsOpen(jid))
            {
                _outgoingHost.NotifyChatChanged(jid);
            }

            string aliasLid = null;
            string aliasPn = null;
            if (_jids.TryGetAlias(jid, out string aliasPair))
            {
                aliasLid = JidHelper.IsLidJid(jid) ? jid : aliasPair;
                aliasPn = JidHelper.IsPhoneJid(jid) ? jid : aliasPair;
            }

            if (chats == null)
            {
                Debug.WriteLine("[MessageFacade] Dropping live list apply: IChatService is not attached.");
                _diagnostics?.Write(
                    "messages",
                    "chat-facade-missing",
                    "messageId=" + (chatMessage.Id ?? string.Empty));
                return;
            }

            LiveIncomingChatListApplyResult listApply = await chats.ApplyLiveIncomingChatListAsync(
                new LiveIncomingChatListApplyRequest
                {
                    ChatJid = jid,
                    PreviewText = request.DisplayContent,
                    Timestamp = chatMessage.Timestamp,
                    PreviewKind = request.PreviewKind,
                    AuthorPrefix = request.ListAuthorPrefix,
                    MentionedJids = chatMessage.MentionedJids,
                    IsFromMe = chatMessage.IsFromMe,
                    SendState = HistoryLiveMessageMapper.FromStatus(chatMessage.Status, chatMessage.IsFromMe),
                    MessageId = chatMessage.Id,
                    ParticipantJid = request.IsGroup ? request.StripParticipantJid : null,
                    SenderName = request.IsGroup && !request.IsFromMe ? request.SenderName : null,
                    UnreadDelta = request.CountsAsUnread ? 1 : 0,
                    CountsAsUnread = request.CountsAsUnread,
                    IsGroup = request.IsGroup,
                    AliasLid = aliasLid,
                    AliasPn = aliasPn
                }).ConfigureAwait(false);

            ChatItem notificationChat = listApply?.Chat;
            int totalUnreadForNotify = listApply != null ? listApply.TotalUnread : 0;

            string notificationName = notificationChat?.Name;
            if (string.IsNullOrWhiteSpace(notificationName))
            {
                notificationName = !string.IsNullOrWhiteSpace(listApply?.DisplayName)
                    ? listApply.DisplayName
                    : _chatState.ResolveDisplayName(jid);
            }

            NotifyLiveIncoming(
                jid,
                notificationName,
                request.SenderName,
                request.NotifyContent ?? request.DisplayContent,
                request.IsGroup,
                request.IsFromMe,
                request.SuppressToast,
                totalUnreadForNotify,
                notificationChat);

            QueueIncomingPersist(jid, chatMessage);
            UnloadMessageCacheIfInactive(jid);
        }

        private void UnloadMessageCacheIfInactive(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || _activeChat.IsOpen(jid))
            {
                return;
            }

            string normalized = JidHelper.Normalize(jid);
            _chatState.MessagesByChat.Remove(normalized);
            if (!string.Equals(normalized, jid, StringComparison.OrdinalIgnoreCase))
            {
                _chatState.MessagesByChat.Remove(jid);
            }
        }

        private async Task PersistPinnedMessageToSqliteAsync(
            string canonical,
            string messageId,
            PendingPin state,
            ChatMessage target)
        {
            if (_historyMessageStore == null ||
                string.IsNullOrWhiteSpace(canonical) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            try
            {
                List<string> keys = new List<string>(ChatPinStoreKeys.For(canonical, _jids));
                var pins = new List<HistoryMessagePinUpdate>(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                {
                    pins.Add(new HistoryMessagePinUpdate
                    {
                        ChatJid = keys[i],
                        MessageId = messageId,
                        IsPinned = state.IsPinned,
                        PinnedAtUtc = state.PinnedAtUtc,
                        PinExpiresAtUtc = state.ExpiresAtUtc
                    });
                }

                await _historyMessageStore.UpsertPinsAsync(pins).ConfigureAwait(false);

                if (target != null)
                {
                    await _outgoingHost.PersistLiveAsync(canonical, target).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] SQLite pin persist failed: " + ex.Message);
            }
        }

        private async Task<ChatMessage> FindPersistedMessageAsync(string chatJid, string messageId)
        {
            if (_historyMessageStore == null ||
                string.IsNullOrWhiteSpace(chatJid) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return null;
            }

            List<string> keys = ResolveChatKeys(chatJid);
            for (int i = 0; i < keys.Count; i++)
            {
                HistoryMessage row = await _historyMessageStore.GetAsync(keys[i], messageId)
                    .ConfigureAwait(false);
                ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                if (mapped != null)
                {
                    return mapped;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public IncomingTimelineAcceptResult AcceptIncomingTimeline(
            string chatJid,
            ChatMessage message,
            bool isGroup)
        {
            return new IncomingTimeline(
                    _chatState.MessagesByChat,
                    _chatState.MessagesByChat.Ids,
                    GetAliasLinkedDirectChatJids,
                    JidHelper.Normalize,
                    TrimInMemoryMessageWindow)
                .Accept(chatJid, message, isGroup);
        }

        /// <inheritdoc />
        public void QueueIncomingPersist(string chatJid, ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || message == null)
            {
                return;
            }

            PendingFlushAction action = _pendingMessages.Add(
                chatJid,
                new[] { message },
                DateTime.UtcNow,
                scheduleFlush: true);
            _pendingFlush.ApplyFlushAction(action);
            _catalog.Schedule();
        }

        /// <inheritdoc />
        public bool EnrichOfflineDuplicateFast(
            string chatJid,
            string messageId,
            bool isFromMe,
            string participantJid,
            string verifiedName,
            string pushName)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            ChatMessage existing = null;
            string persistJid = chatJid;
            ChatMessageCache cache = _chatState.MessagesByChat;

            List<ChatMessage> list;
            if (cache.TryGet(chatJid, out list) && list != null)
            {
                existing = list.FirstOrDefault(
                    m => m != null && string.Equals(m.Id, messageId, StringComparison.Ordinal));
            }

            if (existing == null &&
                AliasLinkedMessageFind.TryFind(
                    cache,
                    GetAliasLinkedDirectChatJids(chatJid),
                    messageId,
                    out string aliasChat,
                    out ChatMessage aliasMsg) &&
                aliasMsg != null)
            {
                existing = aliasMsg;
                persistJid = aliasChat;
            }

            if (existing == null)
            {
                return false;
            }

            DuplicateArrivalPatch patch = DuplicateArrivalEnrichment.Compute(
                existing.Status,
                null,
                isFromMe,
                existing.ParticipantJid,
                JidHelper.Normalize(participantJid),
                existing.SenderName,
                FirstNonBlankText.Pick(verifiedName, pushName));
            if (!DuplicateArrivalEnrichment.Apply(existing, patch))
            {
                return false;
            }

            QueueIncomingPersist(persistJid, existing);
            return true;
        }

        /// <inheritdoc />
        public async Task<bool> ApplyIncomingReactionAsync(
            Message message,
            ChatMessageMapContext context)
        {
            if (message == null || context == null || string.IsNullOrWhiteSpace(context.ChatJid))
            {
                return false;
            }

            string jid = JidHelper.Normalize(context.ChatJid) ?? context.ChatJid;
            IList<ChatMessage> timeline = _chatState.MessagesByChat.GetOrAdd(jid);
            ChatMessage reactionParent = null;
            bool handled = false;

            await _boundMutations.RunAsync(() =>
            {
                handled = TryHandleReaction(message, context, timeline, out reactionParent);
            }).ConfigureAwait(false);

            if (!handled)
            {
                return false;
            }

            if (reactionParent != null)
            {
                try
                {
                    await _outgoingHost.PersistLiveAsync(jid, reactionParent).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MessageFacade] Failed to persist reaction parent: " + ex.Message);
                }

                if (_activeChat.IsOpen(jid))
                {
                    _outgoingHost.NotifyChatChanged(jid);
                }
            }
            else
            {
                Debug.WriteLine(
                    "[MessageFacade] Reaction target not found yet: chat=" + jid +
                    ", id=" + (context.MessageId ?? "<none>"));
            }

            return true;
        }

        /// <inheritdoc />
        public bool HasCachedMessageId(string chatJid, string messageId, bool isGroup)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrEmpty(messageId))
            {
                return false;
            }

            string canonical = _jids.GetCanonicalJid(chatJid) ?? JidHelper.Normalize(chatJid);
            ChatMessageCache cache = _chatState.MessagesByChat;
            if (cache.KnownIds(canonical).Contains(messageId))
            {
                return true;
            }

            if (isGroup)
            {
                return false;
            }

            return AliasLinkedMessageFind.HasId(
                cache,
                GetAliasLinkedDirectChatJids(JidHelper.Normalize(chatJid) ?? chatJid),
                messageId);
        }

        /// <inheritdoc />
        public async Task FinalizeAliasConsolidateAsync(
            string canonicalChatJid,
            string aliasSourceChatJid,
            string messageId,
            ChatMessage consolidatedMessage)
        {
            if (string.IsNullOrWhiteSpace(canonicalChatJid) ||
                string.IsNullOrWhiteSpace(aliasSourceChatJid) ||
                string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            try
            {
                await _messageStore.DeleteMessageAsync(aliasSourceChatJid, messageId)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Alias source delete failed: " + ex.Message);
            }

            if (consolidatedMessage != null)
            {
                try
                {
                    await _outgoingHost.PersistLiveAsync(canonicalChatJid, consolidatedMessage)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MessageFacade] Alias survivor persist failed: " + ex.Message);
                }
            }

            IChatService chats = _resolveChats();
            if (chats != null)
            {
                await chats.DeduplicateChatListAsync("live-direct-alias-duplicate")
                    .ConfigureAwait(false);
            }
        }

        private void TrimInMemoryMessageWindow(string jid) =>
            _chatState.MessagesByChat.Trim(jid, MaxActiveChatMessagesInMemory, DateTime.UtcNow);

        private IReadOnlyList<string> GetAliasCacheKeys(string canonical)
        {
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return Array.Empty<string>();
            }

            var keys = new List<string>();
            foreach (string key in _chatState.MessagesByChat.ChatJids())
            {
                if (string.Equals(_jids.GetCanonicalJid(key), canonical, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }

            return keys;
        }

        private async Task RefreshChatPreviewFromMessageAsync(string canonical, ChatMessage message)
        {
            IChatService chats = _resolveChats();
            if (chats == null || message == null || string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            string selfLabel = _strings != null
                ? _strings.Get("Chat_SelfFallbackName", "You")
                : "You";

            await chats.RefreshChatPreviewAsync(
                canonical,
                message.Content,
                message.Timestamp,
                message.IsFromMe,
                ChatPreviewNormalizer.InferKindFromMessage(message),
                ChatPreviewNormalizer.FormatListAuthorPrefix(
                    message,
                    JidHelper.IsGroupJid(canonical),
                    selfLabel,
                    _jids.GetCanonicalJid(message.ParticipantJid)),
                ParticipantStripIdentity.PreferForDisplay(
                    message.ParticipantJid,
                    _jids.GetCanonicalJid(message.ParticipantJid)),
                message.IsFromMe ? null : message.SenderName).ConfigureAwait(false);
        }

        private IReadOnlyList<string> GetAliasLinkedDirectChatJids(string chatJid)
        {
            return AliasLinkedDirectChats.Collect(
                NameLookupCandidates.Expand(
                    new[] { chatJid },
                    JidHelper.Normalize,
                    normalized =>
                    {
                        string alias;
                        return _jids.TryGetAlias(normalized, out alias) ? alias : null;
                    }),
                JidHelper.Normalize,
                _jids.GetCanonicalJid);
        }
    }
}
