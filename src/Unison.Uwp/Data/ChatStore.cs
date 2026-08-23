using System;

using System.Collections.Concurrent;

using System.Diagnostics;

using System.IO;

using System.Threading;

using System.Threading.Tasks;

using SQLite;

using Unison.Core.Contracts;

using Unison.Core.Helpers;

using Unison.Core.Models;

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



        private readonly ConcurrentDictionary<string, ChatLocalState> _cache =

            new ConcurrentDictionary<string, ChatLocalState>(StringComparer.OrdinalIgnoreCase);



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

            System.Collections.Generic.List<ChatRow> rows =

                await _connection.Table<ChatRow>().ToListAsync().ConfigureAwait(false);

            _cache.Clear();

            if (rows == null)

            {

                return;

            }



            foreach (ChatRow row in rows)

            {

                ChatLocalState state = ToModel(row);

                if (state != null && !string.IsNullOrEmpty(state.Jid))

                {

                    _cache[state.Jid] = Clone(state);

                }

            }



            Debug.WriteLine("[ChatStore] Warm loaded " + _cache.Count + " rows");

        }



        public ChatLocalState TryGetCached(string jid)

        {

            string key = NormalizeJid(jid);

            if (string.IsNullOrEmpty(key))

            {

                return null;

            }



            ChatLocalState cached;

            if (_cache.TryGetValue(key, out cached))

            {

                return Clone(cached);

            }



            return null;

        }



        public async Task<ChatLocalState> GetAsync(string jid)

        {

            string key = NormalizeJid(jid);

            if (string.IsNullOrEmpty(key))

            {

                return null;

            }



            ChatLocalState cached;

            if (_cache.TryGetValue(key, out cached))

            {

                return Clone(cached);

            }



            await EnsureInitializedAsync().ConfigureAwait(false);

            ChatRow row = await _connection.FindAsync<ChatRow>(key).ConfigureAwait(false);

            if (row == null)

            {

                return null;

            }



            ChatLocalState state = ToModel(row);

            _cache[key] = Clone(state);

            return state;

        }



        public Task<ChatLocalState> UpsertAsync(

            string jid,

            ChatLocalStatus status,

            bool isWidgetPinned,

            bool isChatPinned,

            long? mutedUntil)

        {

            return WriteAsync(jid, existing =>

            {

                existing.Status = status;

                existing.IsWidgetPinned = isWidgetPinned;

                existing.IsChatPinned = isChatPinned;

                existing.MutedUntil = mutedUntil;

            });

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

            if (state == null)

            {

                chat.IsWidgetPinned = false;

                // Keep existing MutedUntil / IsChatPinned from history/JSON when no SQLite row yet.

                return;

            }



            chat.LocalStatus = state.Status;

            chat.IsWidgetPinned = state.IsWidgetPinned;

            chat.MutedUntil = state.MutedUntil;

            // ChatStore is the durable local mirror of pin_v1 (ApplyChatPin / app-state).

            // history_chat_preview has no pin columns; without this, a restart showed every

            // chat unpinned until the next app-state sync arrived.

            chat.IsChatPinned = state.IsChatPinned;

            if (!state.IsChatPinned)

            {

                // Match ApplyAppStateChatFlagsAsync: 0 is an explicit unpin tombstone so

                // PN/LID dedupe cannot resurrect a pin from an alias that was not updated.

                chat.PinnedTimestamp = 0;

            }

            else if (chat.PinnedTimestamp == null || chat.PinnedTimestamp == 0)

            {

                // Sort key until app-state fills the real pin timestamp; keep pinned rows on top.

                chat.PinnedTimestamp = 1;

            }

        }



        private async Task<ChatLocalState> WriteAsync(string jid, Action<ChatLocalState> mutate)

        {

            string key = NormalizeJid(jid);

            if (string.IsNullOrEmpty(key) || mutate == null)

            {

                return null;

            }



            await EnsureInitializedAsync().ConfigureAwait(false);

            await _writeLock.WaitAsync().ConfigureAwait(false);

            try

            {

                ChatLocalState next;

                ChatLocalState cached;

                if (_cache.TryGetValue(key, out cached))

                {

                    next = Clone(cached);

                }

                else

                {

                    ChatRow row = await _connection.FindAsync<ChatRow>(key).ConfigureAwait(false);

                    next = row != null ? ToModel(row) : new ChatLocalState { Jid = key };

                }



                next.Jid = key;

                mutate(next);



                await _connection.InsertOrReplaceAsync(ToRow(next)).ConfigureAwait(false);

                _cache[key] = Clone(next);

                return Clone(next);

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



            ChatLocalStatus status = ChatLocalStatus.Active;

            if (Enum.IsDefined(typeof(ChatLocalStatus), row.Status))

            {

                status = (ChatLocalStatus)row.Status;

            }



            return new ChatLocalState

            {

                Jid = row.Jid,

                Status = status,

                IsChatPinned = row.IsChatPinned,

                IsWidgetPinned = row.IsWidgetPinned,

                MutedUntil = row.MutedUntil

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



        private static ChatLocalState Clone(ChatLocalState source)

        {

            if (source == null)

            {

                return null;

            }



            return new ChatLocalState

            {

                Jid = source.Jid,

                Status = source.Status,

                IsChatPinned = source.IsChatPinned,

                IsWidgetPinned = source.IsWidgetPinned,

                MutedUntil = source.MutedUntil

            };

        }

    }

}

