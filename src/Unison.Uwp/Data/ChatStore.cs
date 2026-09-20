using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Data.Entities;
using Windows.Storage;

namespace Unison.Uwp.Data
{
    /// <summary>
    /// SQLite chat metadata store (same unison.db as Person).
    /// </summary>
    public sealed class ChatStore : IChatStore
    {
        private static readonly string DatabaseFileName = "unison.db";

        private readonly ChatLocalStateCache _cache = new ChatLocalStateCache();

        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        private SQLiteAsyncConnection _connection;
        private bool _initialized;

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    return;
                }

                SQLitePCL.Batteries.Init();

                string dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, DatabaseFileName);
                _connection = new SQLiteAsyncConnection(dbPath);
                await _connection.CreateTableAsync<ChatRow>().ConfigureAwait(false);
                _initialized = true;
                Debug.WriteLine("[ChatStore] Initialized at " + dbPath);
            }
            finally
            {
                _initLock.Release();
            }
        }

        public async Task WarmAsync()
        {
            await EnsureInitializedAsync().ConfigureAwait(false);
            List<ChatRow> rows = await _connection.Table<ChatRow>().ToListAsync().ConfigureAwait(false);
            if (rows == null)
            {
                return;
            }
            var states = new List<ChatLocalState>(rows.Count);
            foreach (ChatRow row in rows)
            {
                ChatLocalState state = ToModel(row);
                if (state != null)
                {
                    states.Add(state);
                }
            }

            _cache.LoadWarm(states);

            Debug.WriteLine("[ChatStore] Warm loaded " + _cache.Count + " rows");
        }

        public ChatLocalState TryGetCached(string jid)
        {
            return _cache.TryGet(jid);
        }

        public async Task<ChatLocalState> GetAsync(string jid)
        {
            ChatLocalState cached = _cache.TryGet(jid);
            if (cached != null)
            {
                return cached;
            }

            string key = NormalizeJid(jid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            ChatRow row = await _connection.FindAsync<ChatRow>(key).ConfigureAwait(false);
            if (row == null)
            {
                return null;
            }

            _cache.Seed(ToModel(row));
            return _cache.TryGet(key);
        }

        public Task SetChatPinnedAsync(string jid, bool pinned)
        {
            RememberChatPinned(jid, pinned);
            return WriteAsync(jid, existing => existing.IsChatPinned = pinned);
        }

        public Task SetWidgetPinnedAsync(string jid, bool pinned)
        {
            return WriteAsync(jid, existing => existing.IsWidgetPinned = pinned);
        }

        public Task SetMutedUntilAsync(string jid, long? mutedUntil)
        {
            RememberMutedUntil(jid, mutedUntil);
            return WriteAsync(jid, existing => existing.MutedUntil = mutedUntil);
        }

        public Task SetStatusAsync(string jid, ChatStatus status)
        {
            return WriteAsync(jid, existing => existing.Status = status);
        }

        public void RememberChatPinned(string jid, bool pinned)
        {
            _cache.Mutate(jid, state => state.IsChatPinned = pinned);
        }

        public void RememberMutedUntil(string jid, long? mutedUntil)
        {
            _cache.Mutate(jid, state => state.MutedUntil = mutedUntil);
        }

        public void ApplyTo(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            ChatLocalState state = TryGetCached(chat.JID);
            ApplyLocalFields(chat, state);
        }

        public async Task ApplyToAsync(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            ChatLocalState state = await GetAsync(chat.JID).ConfigureAwait(false);
            ApplyLocalFields(chat, state);
        }

        /// <summary>
        /// Applies widget pin, chat-list pin, and mutedUntil from store onto the model.
        /// </summary>
        private static void ApplyLocalFields(ChatItem chat, ChatLocalState state)
        {
            ChatLocalStateApply.Apply(chat, state);
        }

        /// <summary>
        /// Writes one field, and persists the row the cache holds after that write is merged in.
        /// </summary>
        /// <remarks>
        /// The merge has to happen here, next to the INSERT, rather than on a copy taken before it.
        /// Pin and mute arrive from two senders that overlap during a sync (app-state patches and
        /// the history Conversation), so a writer that read the state, awaited SQLite and then put
        /// its own copy back was undoing whatever the other one remembered inside that await —
        /// in the cache, which <see cref="ApplyTo"/> reads, and in the row, which
        /// <c>InsertOrReplace</c> rewrites whole.
        /// </remarks>
        private async Task<ChatLocalState> WriteAsync(string jid, Action<ChatLocalState> mutate)
        {
            string key = NormalizeJid(jid);
            if (string.IsNullOrWhiteSpace(key) || mutate == null)
            {
                return null;
            }

            await EnsureInitializedAsync().ConfigureAwait(false);
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_cache.Contains(key))
                {
                    ChatRow row = await _connection.FindAsync<ChatRow>(key).ConfigureAwait(false);
                    _cache.Seed(ToModel(row));
                }

                ChatLocalState next = _cache.Mutate(key, mutate);
                if (next == null)
                {
                    return null;
                }

                await _connection.InsertOrReplaceAsync(ToRow(next)).ConfigureAwait(false);
                return next;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync().ConfigureAwait(false);
            }
        }

        private static string NormalizeJid(string jid)
        {
            return JidHelper.Normalize(jid);
        }

        private static ChatLocalState ToModel(ChatRow row)
        {
            if (row == null)
            {
                return null;
            }

            ChatStatus status = ChatStatus.Active;
            if (Enum.IsDefined(typeof(ChatStatus), row.Status))
            {
                status = (ChatStatus)row.Status;
            }

            return new ChatLocalState
            {
                Jid = row.Jid,
                Status = status,
                IsChatPinned = row.IsChatPinned,
                IsWidgetPinned = row.IsWidgetPinned,
                MutedUntil = row.MutedUntil,
                KnownFields = ChatLocalStateFields.All
            };
        }

        private static ChatRow ToRow(ChatLocalState state)
        {
            return new ChatRow
            {
                Jid = state.Jid,
                Status = (int)state.Status,
                IsChatPinned = state.IsChatPinned,
                IsWidgetPinned = state.IsWidgetPinned,
                MutedUntil = state.MutedUntil,
                UpdatedAtUtc = DateTime.UtcNow
            };
        }

    }
}
