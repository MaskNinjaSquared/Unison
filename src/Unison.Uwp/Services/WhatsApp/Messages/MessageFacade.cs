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
using Unison.Socket.Abstractions;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;
using Unison.Uwp.Services.WhatsApp.History;
using Unison.Uwp.Services;

namespace Unison.Uwp.Services.WhatsApp.Messages
{
    /// <summary>
    /// Message facade: Person upsert, domain <see cref="ChatMessage"/> construction, then WA transport.
    /// Prefer growing APIs here (e.g. <see cref="GetChatMessage"/>) instead of in WhatsAppService.
    /// </summary>
    public sealed class MessageFacade : IMessageService
    {
        private readonly IPersonStore _personStore;
        private readonly IWhatsAppService _whatsAppService;
        private readonly IAppStateLocalHost _localAppState;
        private readonly IChatMessageMapper _chatMessageMapper;
        private readonly IReactionMapper _reactionMapper;
        private readonly HistoryFacade _history;
        private readonly IHistoryMessageStore _historyMessageStore;
        private readonly IJidResolver _jids;
        private readonly IChatStateStore _chatState;
        private readonly INotificationService _notifications;
        private readonly IChatStore _chatStore;
        private readonly OutgoingTextSend _textSend;
        private readonly OutgoingMediaSend _mediaSend;
        private readonly MediaAvailability _availability;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IOutgoingMessageHost _outgoingHost;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly int _sqlOpenPageSize;
        private readonly int _sqlLoadMorePageSize;
        private readonly int _thinTimelineThreshold;

        internal MessageFacade(
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            IAppStateLocalHost localAppState,
            IChatMessageMapper chatMessageMapper,
            IReactionMapper reactionMapper,
            HistoryFacade history,
            IHistoryMessageStore historyMessageStore,
            IJidResolver jids,
            INotificationService notifications,
            IOutgoingMessageHost outgoingHost,
            IMediaCache mediaCache,
            MediaDerivationService derivation,
            MediaDownloadGate downloadGate,
            IMediaDownloadHost mediaHost,
            IEncryptedMediaDownloader mediaDownloader,
            IChatCatalogPersistence catalog,
            IWhatsAppSessionProvider sessions,
            IChatStore chatStore = null,
            IChatStateStore chatState = null,
            ISystemInfoProvider systemInfo = null,
            IRuntimeDiagnostics diagnostics = null)
        {
            _diagnostics = diagnostics;
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _localAppState = localAppState ?? throw new ArgumentNullException(nameof(localAppState));
            _chatMessageMapper = chatMessageMapper ?? throw new ArgumentNullException(nameof(chatMessageMapper));
            _reactionMapper = reactionMapper ?? throw new ArgumentNullException(nameof(reactionMapper));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _historyMessageStore = historyMessageStore
                ?? throw new ArgumentNullException(nameof(historyMessageStore));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _chatStore = chatStore;
            _chatState = chatState;
            _outgoingHost = outgoingHost ?? throw new ArgumentNullException(nameof(outgoingHost));

            _textSend = new OutgoingTextSend(_outgoingHost, _jids);
            _mediaSend = new OutgoingMediaSend(
                _outgoingHost,
                _jids,
                mediaCache ?? throw new ArgumentNullException(nameof(mediaCache)));
            _availability = new MediaAvailability(
                _whatsAppService,
                mediaHost ?? throw new ArgumentNullException(nameof(mediaHost)),
                mediaDownloader ?? throw new ArgumentNullException(nameof(mediaDownloader)),
                _jids,
                mediaCache,
                derivation ?? throw new ArgumentNullException(nameof(derivation)),
                downloadGate ?? throw new ArgumentNullException(nameof(downloadGate)),
                _catalog);
            // systemInfo kept in the ctor for DI compatibility; page sizes match desktop on all devices.
            _ = systemInfo;

            // Same SQLite page sizes as desktop (Mobile used to use 30/20).
            _sqlOpenPageSize = 50;
            _sqlLoadMorePageSize = 30;
            _thinTimelineThreshold = Math.Max(5, _sqlOpenPageSize - ThinTimelineOnDemandMargin);

            // Both live as long as the app does, so there is nothing to unhook from.
            _whatsAppService.OnChatMessagesChanged += (s, jid) => Relay(() => ChatMessagesChanged?.Invoke(this, jid), "ChatMessagesChanged");
            _whatsAppService.OnPresenceUpdate += (s, e) => Relay(() => PresenceUpdated?.Invoke(this, e), "PresenceUpdated");
        }

        public event EventHandler<string> ChatMessagesChanged;

        public event EventHandler<PresenceUpdateEventArgs> PresenceUpdated;

        public Task SubscribeToPresenceAsync(string jid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (string.IsNullOrWhiteSpace(jid) || socket == null || !socket.IsConnected)
            {
                return Task.CompletedTask;
            }

            return socket.PresenceSubscribeAsync(jid);
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

        public async Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            await _whatsAppService.EnsureConnectedAsync();
            if (message == null || string.IsNullOrWhiteSpace(message.Id))
            {
                throw new ArgumentException("A valid message is required", nameof(message));
            }

            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null)
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

            await socket.SendPinInChatMessageAsync(canonical, key, pin, durationSeconds);
            MessagePinWindow window = MessagePinWindow.FromLocal(pin, durationSeconds, DateTime.UtcNow);
            await _localAppState.ApplyMessagePinAsync(
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

            if (_chatState != null && pushPairs.Count > 0)
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
    }
}
