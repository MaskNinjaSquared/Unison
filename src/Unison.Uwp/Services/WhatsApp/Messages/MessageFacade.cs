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
using Unison.Uwp.Services.WhatsApp.History;

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
        private readonly IChatMessageMapper _chatMessageMapper;
        private readonly IReactionMapper _reactionMapper;
        private readonly HistoryFacade _history;
        private readonly IHistoryMessageStore _historyMessageStore;

        public MessageFacade(
            IPersonStore personStore,
            IWhatsAppService whatsAppService,
            IChatMessageMapper chatMessageMapper,
            IReactionMapper reactionMapper,
            HistoryFacade history,
            IHistoryMessageStore historyMessageStore)
        {
            _personStore = personStore ?? throw new ArgumentNullException(nameof(personStore));
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _chatMessageMapper = chatMessageMapper ?? throw new ArgumentNullException(nameof(chatMessageMapper));
            _reactionMapper = reactionMapper ?? throw new ArgumentNullException(nameof(reactionMapper));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _historyMessageStore = historyMessageStore
                ?? throw new ArgumentNullException(nameof(historyMessageStore));

            // Both live as long as the app does, so there is nothing to unhook from.
            _whatsAppService.OnChatMessagesChanged += (s, jid) => Relay(() => ChatMessagesChanged?.Invoke(this, jid), "ChatMessagesChanged");
            _whatsAppService.OnPresenceUpdate += (s, e) => Relay(() => PresenceUpdated?.Invoke(this, e), "PresenceUpdated");
        }

        public event EventHandler<string> ChatMessagesChanged;

        public event EventHandler<PresenceUpdateEventArgs> PresenceUpdated;

        public Task SubscribeToPresenceAsync(string jid)
        {
            return _whatsAppService.PresenceSubscribeAsync(jid);
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
        /// Below this local count, opening a chat asks the phone for older history
        /// (RECENT sync often leaves only the list-preview message).
        /// </summary>
        private const int ThinTimelineOnDemandThreshold = 40;
        private const int OnDemandFetchCount = 80;
        private const int SqlOpenPageSize = 100;
        private const int SqlLoadMorePageSize = 30;

        public async Task<List<ChatMessage>> LoadMessagesForChatAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return new List<ChatMessage>();
            }

            List<ChatMessage> fromClient = null;
            try
            {
                fromClient = await _whatsAppService.LoadMessagesForChatAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Client LoadMessagesForChat failed: " + ex.Message);
            }

            IReadOnlyList<HistoryMessage> fromSql = null;
            try
            {
                fromSql = await LoadSqlHistoryRowsAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] SQLite history message load failed: " + ex.Message);
            }

            var byId = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            var merged = new List<ChatMessage>();

            void AddRange(IEnumerable<ChatMessage> source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var message in source)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(message.Id))
                    {
                        merged.Add(message);
                        continue;
                    }

                    if (!byId.ContainsKey(message.Id))
                    {
                        byId[message.Id] = message;
                        merged.Add(message);
                    }
                }
            }

            if (fromSql != null)
            {
                foreach (var row in fromSql)
                {
                    ChatMessage mapped = HistoryMessageMapper.ToChatMessage(row);
                    if (mapped != null)
                    {
                        AddRange(new[] { mapped });
                    }
                }
            }

            // Client/live rows win on id collision (newer status / media URIs),
            // but keep SQLite media keys when the winner has none.
            if (fromClient != null)
            {
                foreach (var message in fromClient)
                {
                    if (message == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(message.Id))
                    {
                        merged.Add(message);
                        continue;
                    }

                    ChatMessage existing;
                    if (byId.TryGetValue(message.Id, out existing))
                    {
                        HistoryMessageMapper.CopyMediaKeysIfMissing(message, existing);
                        HistoryMessageMapper.CopyForwardedIfMissing(message, existing);
                        HistoryMessageMapper.CopyQuotedParticipantIfMissing(message, existing);
                    }

                    byId[message.Id] = message;
                }

                merged = byId.Values
                    .Concat(merged.Where(m => string.IsNullOrWhiteSpace(m.Id)))
                    .GroupBy(m => m.Id ?? Guid.NewGuid().ToString("N"), StringComparer.Ordinal)
                    .Select(g => g.First())
                    .OrderBy(m => m.Timestamp)
                    .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                    .ToList();
            }
            else
            {
                merged = merged
                    .OrderBy(m => m.Timestamp)
                    .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                    .ToList();
            }

            if (merged.Count > 0)
            {
                try
                {
                    // Seed even when rows came only from live/client so on-demand has a cursor.
                    await _whatsAppService.SeedChatMessagesInMemoryAsync(jid, merged).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[MessageFacade] SeedChatMessagesInMemory failed: " + ex.Message);
                }
            }

            // RECENT history often leaves a single listable message — pull older on open.
            if (merged.Count > 0 &&
                merged.Count < ThinTimelineOnDemandThreshold &&
                !IsHistoryOnDemandPending(jid))
            {
                _ = RequestOlderHistorySafeAsync(jid, merged.Count);
            }

            return merged;
        }

        private async Task RequestOlderHistorySafeAsync(string jid, int localCount)
        {
            try
            {
                bool requested = await EnsureHistoryOnDemandAsync(jid, OnDemandFetchCount).ConfigureAwait(false);
                Debug.WriteLine(
                    "[MessageFacade] Thin timeline on-demand for " + jid +
                    " local=" + localCount + " requested=" + requested);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MessageFacade] Thin timeline on-demand failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Every SQLite key a chat can be stored under: the JID itself, its canonical form, and the
        /// LID/PN alias. History rows land under whichever one the chunk carried.
        /// </summary>
        private List<string> ResolveChatKeys(string jid)
        {
            var keys = new List<string>();
            void AddKey(string key)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                string norm = JidHelper.Normalize(key);
                if (string.IsNullOrWhiteSpace(norm))
                {
                    return;
                }

                for (int i = 0; i < keys.Count; i++)
                {
                    if (string.Equals(keys[i], norm, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                keys.Add(norm);
            }

            AddKey(jid);
            try
            {
                AddKey(_whatsAppService.GetCanonicalJid(jid));
            }
            catch
            {
            }

            try
            {
                string norm = JidHelper.Normalize(jid);
                if (!string.IsNullOrWhiteSpace(norm) &&
                    _whatsAppService.JidAlias != null &&
                    _whatsAppService.JidAlias.TryGetValue(norm, out string alias))
                {
                    AddKey(alias);
                }
            }
            catch
            {
            }

            return keys;
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
                cached = await _whatsAppService.LoadMessagesForChatAsync(jid).ConfigureAwait(false);
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
            return await LoadSqlHistoryPageAsync(jid, SqlOpenPageSize, null, null).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<HistoryMessage>> LoadSqlHistoryPageAsync(
            string jid,
            int limit,
            DateTime? beforeUtc,
            string beforeMessageId)
        {
            List<string> keys = ResolveChatKeys(jid);
            var byId = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);
            var ordered = new List<HistoryMessage>();
            foreach (string key in keys)
            {
                IReadOnlyList<HistoryMessage> rows =
                    await _historyMessageStore.GetForChatAsync(key, limit, beforeUtc, beforeMessageId)
                        .ConfigureAwait(false);
                if (rows == null)
                {
                    continue;
                }

                foreach (var row in rows)
                {
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

            if (ordered.Count == 0)
            {
                return ordered;
            }

            List<HistoryMessage> page = ordered
                .OrderByDescending(r => r.TimestampUtc ?? DateTime.MinValue)
                .ThenByDescending(r => r.MessageId, StringComparer.Ordinal)
                .Take(limit)
                .Reverse()
                .ToList();

            if (!beforeUtc.HasValue)
            {
                var pageById = new Dictionary<string, HistoryMessage>(StringComparer.Ordinal);
                for (int i = 0; i < page.Count; i++)
                {
                    if (page[i] != null && !string.IsNullOrWhiteSpace(page[i].MessageId))
                    {
                        pageById[page[i].MessageId] = page[i];
                    }
                }

                foreach (string key in keys)
                {
                    await MergeSqlExtrasAsync(key, pageById, page).ConfigureAwait(false);
                }
            }

            return page;
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
                rows = await LoadSqlHistoryPageAsync(jid, SqlLoadMorePageSize, beforeUtc, beforeMessageId)
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
            return _whatsAppService.EnsureHistoryOnDemandAsync(jid, count);
        }

        public bool IsHistoryOnDemandPending(string jid)
        {
            return _whatsAppService.IsHistoryOnDemandPending(jid);
        }

        public Task<ChatMessage> SendTextMessageAsync(string jid, string text)
        {
            return _whatsAppService.SendTextMessageAsync(jid, text);
        }

        public Task SendImageAsync(string jid, byte[] imageBytes, string caption)
        {
            return _whatsAppService.SendImageAsync(jid, imageBytes, caption);
        }

        public Task<ChatMessage> SendAudioMessageAsync(string jid, byte[] audioBytes, string mimeType, uint durationSeconds, bool isVoiceMessage = false)
        {
            return _whatsAppService.SendAudioMessageAsync(jid, audioBytes, mimeType, durationSeconds, isVoiceMessage);
        }

        public Task<string> EnsureAudioAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureAudioAvailableAsync(message);
        }

        public Task<string> EnsureImageAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureImageAvailableAsync(message);
        }

        public Task<string> EnsureVideoAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureVideoAvailableAsync(message);
        }

        public Task<string> EnsureDocumentAvailableAsync(ChatMessage message)
        {
            return _whatsAppService.EnsureDocumentAvailableAsync(message);
        }

        public Task SetMessagePinnedAsync(string chatJid, ChatMessage message, bool pin, uint durationSeconds = 604800)
        {
            return _whatsAppService.SetMessagePinnedAsync(chatJid, message, pin, durationSeconds);
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

            return true;
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
        /// Inserts/updates Person rows. UpsertIfChanged skips writes when nothing changed.
        /// </summary>
        private async Task UpsertPeopleFromHistoryAsync(HistorySync sync)
        {
            await _personStore.InitializeAsync().ConfigureAwait(false);

            int writes = 0;

            if (sync.Pushnames != null)
            {
                foreach (var pn in sync.Pushnames)
                {
                    if (string.IsNullOrWhiteSpace(pn?.Id) || string.IsNullOrWhiteSpace(pn.Pushname_))
                    {
                        continue;
                    }

                    string jid = JidHelper.Normalize(pn.Id);
                    string phone = JidHelper.TryPhoneFromJid(jid);
                    if (await _personStore.UpsertIfChangedAsync(
                        jid,
                        pn.Pushname_,
                        null,
                        phone,
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

            Debug.WriteLine("[MessageFacade] Person upserts from history: " + writes);
        }

        public Task ResyncConversationsAsync(System.IProgress<ConversationResyncPhase> progress = null)
        {
            // The facade falls back to the service on its own when the legacy stack is the one
            // connected, so there is no flag to read here.
            return _history.ResyncConversationsAsync(progress);
        }

        public void StartNewChat(string jid)
        {
            _whatsAppService.StartNewChat(jid);
        }
    }
}
