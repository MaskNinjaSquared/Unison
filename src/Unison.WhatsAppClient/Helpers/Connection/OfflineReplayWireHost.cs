using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Proto;
using Unison.Baileys.Client;
using Unison.Core.Contracts;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// History-sync wire apply, the pending-notification (offline replay) release and the
    /// per-open watchdog that forces a release when the server never sends one, plus the
    /// first-release latch they share. The transport stays behind
    /// <see cref="IConnectionSocketControl"/>.
    /// </summary>
    public sealed class OfflineReplayWireHost
    {
        private const string LogPrefix = "[OfflineReplayWireHost] ";

        private readonly Func<IHistoryWireIngress> _resolveHistory;
        private readonly Func<IMessageWireIngress> _resolveMessages;
        private readonly IConnectionSocketControl _socket;
        private readonly ConnectionSessionFlags _sessionFlags;
        private readonly ConnectionSlot<AuthState> _authSlot;
        private readonly SyncWorkGate _syncWork;
        private readonly ChatCatalogPersistenceHost _catalogPersistence;
        private readonly StartupMaintenanceHost _startupMaintenance;
        private readonly HistoryCatchUpHost _historyCatchUp;
        private readonly IRuntimeDiagnostics _diagnostics;

        private readonly OfflineReplayFirstReleaseLatch _latch = new OfflineReplayFirstReleaseLatch();
        private readonly HistorySyncWireOwner _historySyncWire;
        private readonly OfflineSyncedWatchdog _watchdog;
        private readonly OfflineReplayReleaseOwner _release;

        public OfflineReplayWireHost(
            Func<IHistoryWireIngress> resolveHistory,
            Func<IMessageWireIngress> resolveMessages,
            IConnectionSocketControl socket,
            ConnectionSessionFlags sessionFlags,
            ConnectionSlot<AuthState> authSlot,
            SyncWorkGate syncWork,
            ChatCatalogPersistenceHost catalogPersistence,
            StartupMaintenanceHost startupMaintenance,
            HistoryCatchUpHost historyCatchUp,
            IRuntimeDiagnostics diagnostics)
        {
            _resolveHistory = resolveHistory
                ?? throw new ArgumentNullException(nameof(resolveHistory));
            _resolveMessages = resolveMessages
                ?? throw new ArgumentNullException(nameof(resolveMessages));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _sessionFlags = sessionFlags ?? throw new ArgumentNullException(nameof(sessionFlags));
            _authSlot = authSlot ?? throw new ArgumentNullException(nameof(authSlot));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _catalogPersistence = catalogPersistence
                ?? throw new ArgumentNullException(nameof(catalogPersistence));
            _startupMaintenance = startupMaintenance
                ?? throw new ArgumentNullException(nameof(startupMaintenance));
            _historyCatchUp = historyCatchUp
                ?? throw new ArgumentNullException(nameof(historyCatchUp));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

            _historySyncWire = new HistorySyncWireOwner(
                () => _sessionFlags.FatalSessionEnded,
                () => _authSlot.Current != null,
                (conversations, syncType) =>
                    _diagnostics.Write(
                        "history",
                        "sync-apply-begin",
                        "conversations=" + conversations + "; type=" + syncType),
                (conversations, elapsedMs) =>
                    _diagnostics.Write(
                        "history",
                        "sync-apply-end",
                        "conversations=" + conversations + "; elapsedMs=" + elapsedMs),
                ApplyHistorySyncAsync);

            _watchdog = new OfflineSyncedWatchdog(
                () => _socket.IsConnected,
                () => _latch.HasReleased,
                generation =>
                {
                    _diagnostics.Write(
                        "connection",
                        "offline-synced-watchdog",
                        "fired|generation=" + generation);
                    _ = _release.RunAsync(0);
                },
                detail => _diagnostics.Write(
                    "connection",
                    "offline-synced-watchdog",
                    detail));

            _release = new OfflineReplayReleaseOwner(
                () => _latch.TryMarkFirstRelease(),
                (offlineCount, firstRelease) =>
                {
                    Debug.WriteLine(
                        LogPrefix + "Received pending-notification replay release (" +
                        offlineCount + " messages); first=" + firstRelease);
                    _diagnostics.Write(
                        "connection",
                        "offline-replay-release",
                        "count=" + offlineCount + "; first=" + firstRelease);
                    _watchdog.Cancel("offline-replay-release");
                },
                () => _syncWork.SetReplayDrainPending(false),
                FlushOfflineReplayAsync,
                () => _socket.IsConnected,
                offlineCount =>
                    _diagnostics.Write(
                        "connection",
                        "offline-replay-synced-skipped",
                        "reason=not-connected; count=" + offlineCount +
                        "; socketConnected=" + _socket.IsTransportOpen +
                        "; handshake=" + _socket.IsHandshakeComplete),
                NotifyMessagesReleasedAsync,
                _startupMaintenance.SchedulePostReplay,
                () => _historyCatchUp.IsHistoryCatchUpActive,
                offlineCount => _ = _historyCatchUp.PostOfflineFreshness.RunAsync(offlineCount));
        }

        public HistorySyncWireOwner HistorySyncWire => _historySyncWire;

        public OfflineReplayReleaseOwner OfflineReplayRelease => _release;

        /// <summary>A new socket is being prepared: the next release counts as the first again.</summary>
        public void ResetForNewOpen() => _latch.ResetForNewOpen();

        /// <summary>Arms or cancels the watchdog from a published connection status.</summary>
        public void SyncWatchdogWithStatus(string status)
        {
            if (string.Equals(status, "open", StringComparison.Ordinal))
            {
                // Connect prepare already resets the latch for a new socket. If pending
                // notifications arrived before this open event, HasReleased is true — do not
                // clear it or arm a watchdog that would force a second release.
                if (_latch.HasReleased)
                {
                    return;
                }

                _latch.ResetForNewOpen();
                _watchdog.Arm();
                return;
            }

            if (string.Equals(status, "synced", StringComparison.Ordinal) ||
                string.Equals(status, "close", StringComparison.Ordinal) ||
                string.Equals(status, "closed", StringComparison.Ordinal) ||
                string.Equals(status, "reconnecting", StringComparison.Ordinal) ||
                string.Equals(status, "connecting", StringComparison.Ordinal) ||
                string.Equals(status, "background", StringComparison.Ordinal) ||
                string.Equals(status, "suspended", StringComparison.Ordinal))
            {
                _watchdog.Cancel(status);
            }
        }

        private Task ApplyHistorySyncAsync(HistorySync sync)
        {
            IHistoryWireIngress history = _resolveHistory();
            if (history == null)
            {
                throw new InvalidOperationException(
                    "IHistoryService must resolve before history sync apply.");
            }

            return history.ApplyHistorySyncAsync(sync);
        }

        private async Task FlushOfflineReplayAsync(string reason)
        {
            try
            {
                await _catalogPersistence.OfflineReplayFlush
                    .FlushOfflineReplayAsync(reason)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    LogPrefix + "Non-fatal offline replay message flush failure after offline drain: " +
                    ex.Message);
            }
        }

        private async Task NotifyMessagesReleasedAsync(
            int offlineCount,
            bool firstRelease,
            bool transportReady)
        {
            try
            {
                IMessageWireIngress messages = _resolveMessages();
                if (messages != null)
                {
                    await messages.NotifyOfflineReplayReleasedAsync(
                        offlineCount,
                        firstRelease,
                        transportReady).ConfigureAwait(false);
                }
                else
                {
                    Debug.WriteLine(LogPrefix + "Offline settle skipped: MessageFacade not attached.");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(LogPrefix + "Non-fatal offline replay settle failure: " + ex.Message);
            }
        }
    }
}
