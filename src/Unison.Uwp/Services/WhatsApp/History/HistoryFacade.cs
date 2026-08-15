// =============================================================================
// HistoryFacade
//
// Everything the app asks the phone for about the past. Today that is the full
// resync - "forget everything and download it again" - and the on-demand fetch
// of older messages belongs here as it moves off WhatsAppService.
//
// The wipe half is local and the refill half is not: WhatsApp never re-runs the
// initial bootstrap for a companion that is already linked, so the only way back
// to a full history is to ask the phone for one and wait for it to answer.
//
// What the legacy path could not do was tell whether it had been answered. It
// sent the request, waited on a timer, and forced a reconnect when the timer ran
// out - guessing, because nothing tied the chunks that arrived to the request
// that asked for them. The socket rewrite carries that tie, so this waits on the
// answer itself and reports failure when there is none.
// =============================================================================
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Socket.Events;
using Unison.Socket.Session;
using Unison.Socket.Sync;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.History
{
    public sealed class HistoryFacade : IHistoryService
    {
        /// <summary>How long the phone gets to send anything at all before we call it a failure.</summary>
        private static readonly TimeSpan FirstChunkTimeout = TimeSpan.FromSeconds(75);

        /// <summary>
        /// A gap this long after chunks were flowing means the phone is done. It has no way to
        /// say so: the last chunk of a sync looks exactly like the ones before it.
        /// </summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        /// <summary>A ceiling, so a phone that dribbles chunks forever cannot pin the overlay open.</summary>
        private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(6);

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IWhatsAppService _appState;
        private readonly IMessageStore _store;
        private readonly ILocalSettings _settings;

        internal HistoryFacade(
            IWhatsAppSessionProvider sessions,
            IWhatsAppService appState,
            IMessageStore store,
            ILocalSettings settings)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException(nameof(sessions));
            }

            if (appState == null)
            {
                throw new ArgumentNullException(nameof(appState));
            }

            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _sessions = sessions;
            _appState = appState;
            _store = store;
            _settings = settings;

            // Both live as long as the app does, so there is nothing to unhook from.
            _appState.OnSyncStatus += (s, status) => Relay(() => SyncStatusChanged?.Invoke(this, status), "SyncStatusChanged");
            _appState.OnHistorySyncReceived += (s, sync) => Relay(() => HistorySyncReceived?.Invoke(this, sync), "HistorySyncReceived");
            _appState.OnInitialSyncProgress += (s, e) => Relay(() => InitialSyncProgress?.Invoke(this, e), "InitialSyncProgress");
        }

        public event EventHandler<string> SyncStatusChanged;

        public event EventHandler<global::Proto.HistorySync> HistorySyncReceived;

        public event EventHandler<InitialSyncProgressEventArgs> InitialSyncProgress;

        private static void Relay(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] " + name + " handler failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Wipes the local conversations and asks the phone to send them again. The account stays
        /// linked; only what we hold locally is thrown away.
        /// </summary>
        public async Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null)
        {
            var socket = _sessions.Socket;
            if (socket == null)
            {
                // No socket to correlate an answer through. The service's own resync knows how to
                // force a fresh transport first, which is the one thing worth trying from here.
                await _appState.ResyncConversationsAsync(progress).ConfigureAwait(false);
                return;
            }

            progress?.Report(ConversationResyncPhase.CleaningHistory);
            await WipeLocalConversationsAsync().ConfigureAwait(false);

            progress?.Report(ConversationResyncPhase.PreparingConversations);
            _appState.RaiseSyncStatus("Re-syncing conversations...");

            try
            {
                await _appState.EnsureConnectedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not connect before requesting history: " + ex.Message);
                _appState.RaiseSyncStatus("Could not start history download. Try again.");
                return;
            }

            // Re-read both: connecting may well have replaced the session we checked above.
            socket = _sessions.Socket;
            var session = _sessions.Current;
            if (socket == null || session == null)
            {
                _appState.RaiseSyncStatus("Could not start history download. Try again.");
                return;
            }

            var answered = await RequestAndAwaitHistoryAsync(socket, session).ConfigureAwait(false);
            if (answered)
            {
                // The latch only exists to make a later connection retry a request that was never
                // answered. It was, so it would only cause a second full download.
                TrySetForceHistoryRepair(false);
                _appState.RaiseSyncStatus(null);
                return;
            }

            Debug.WriteLine("[ConversationResync] The phone did not answer with history.");
            _appState.RaiseSyncStatus("History did not arrive. It will be retried on the next connection.");
        }

        private async Task WipeLocalConversationsAsync()
        {
            await _store.WipeChatsAndMessagesAsync().ConfigureAwait(false);
            await _appState.ClearConversationCachesAsync().ConfigureAwait(false);

            try
            {
                await _store.SaveChatsAsync(Enumerable.Empty<ChatItem>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not persist the empty chat list: " + ex.Message);
            }

            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not clear notifications: " + ex.Message);
            }

            // Set before the request rather than after it: if the app dies between the wipe and
            // the answer, this is what stops it from starting up permanently empty.
            TrySetForceHistoryRepair(true);
        }

        /// <summary>
        /// Sends the request and waits for the history it produces, rather than for a fixed time.
        /// </summary>
        private async Task<bool> RequestAndAwaitHistoryAsync(IWhatsAppSocket socket, WhatsAppSession session)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long lastChunkTicks = 0;

            // The event bus rather than the socket's legacy history event: only the chunk the bus
            // publishes carries the session id, and that id is the whole reason this can wait for
            // an answer instead of for a clock.
            using (session.Events.Process(batch =>
            {
                MessagingHistorySet chunk;
                if (batch.TryGet(WaEventKind.MessagingHistorySet, out chunk) &&
                    BelongsToRequest(chunk, requestId))
                {
                    Interlocked.Exchange(ref lastChunkTicks, DateTime.UtcNow.Ticks);

                    // The phone says how far along it is, so we do not have to infer the end from
                    // silence when it bothers to tell us.
                    if (chunk.Progress.HasValue && chunk.Progress.Value >= 100)
                    {
                        completed.TrySetResult(true);
                    }
                }

                return Task.CompletedTask;
            }))
            {
                try
                {
                    await socket.RequestFullHistorySyncOnDemandAsync(null, requestId).ConfigureAwait(false);
                    Debug.WriteLine("[ConversationResync] Requested a full history sync, requestId=" + requestId);

                    return await WaitForChunksAsync(completed.Task, () => Interlocked.Read(ref lastChunkTicks))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ConversationResync] The history request failed: " + ex.Message);
                    return false;
                }
            }
        }

        private static async Task<bool> WaitForChunksAsync(Task<bool> completed, Func<long> lastChunkTicks)
        {
            var startedUtc = DateTime.UtcNow;

            while (DateTime.UtcNow - startedUtc < HardTimeout)
            {
                var finished = await Task.WhenAny(completed, Task.Delay(PollInterval)).ConfigureAwait(false);
                if (ReferenceEquals(finished, completed))
                {
                    return true;
                }

                var ticks = lastChunkTicks();
                if (ticks == 0)
                {
                    if (DateTime.UtcNow - startedUtc > FirstChunkTimeout)
                    {
                        return false;
                    }

                    continue;
                }

                if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > IdleTimeout)
                {
                    return true;
                }
            }

            // Out of time, but history did arrive and is already on screen.
            return lastChunkTicks() != 0;
        }

        /// <summary>
        /// Whether a chunk is the answer to our request. The session id settles it when the phone
        /// echoes one; when it does not, everything just got wiped, so any history carrying
        /// conversations can only be what we asked for.
        /// </summary>
        private static bool BelongsToRequest(MessagingHistorySet chunk, string requestId)
        {
            if (chunk == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(chunk.PeerDataRequestSessionId))
            {
                return string.Equals(chunk.PeerDataRequestSessionId, requestId, StringComparison.Ordinal);
            }

            return chunk.Chats != null && chunk.Chats.Count > 0;
        }

        private void TrySetForceHistoryRepair(bool pending)
        {
            try
            {
                _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, pending);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not update the force-history flag: " + ex.Message);
            }
        }
    }
}
