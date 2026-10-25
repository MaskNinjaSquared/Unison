using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Debug / diagnostics pane (session log, runtime health, verbose toggle, wipe session).
    /// Confirm dialogs use <see cref="IDialogService"/> (Imgur pattern).
    /// </summary>
    public class DebugViewModel : Observable
    {
        private const int MaxDisplayedLogCharacters = 60000;
        private const int FrugalMaxDisplayedLogCharacters = 12000;

        /// <summary>Live log drain interval when the host is not on a frugal budget.</summary>
        public static readonly TimeSpan GenerousLogFlushInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Live log drain on Mobile/frugal — avoids rebinding a huge TextBlock several times
        /// per second while catch-up / name refresh storms the journal.
        /// </summary>
        public static readonly TimeSpan FrugalLogFlushInterval = TimeSpan.FromSeconds(1);

        /// <summary>Runtime health rebuild interval (desktop / high).</summary>
        public static readonly TimeSpan GenerousRuntimeRefreshInterval = TimeSpan.FromSeconds(2);

        /// <summary>Runtime health rebuild on frugal — snapshot+recent is expensive on ARM.</summary>
        public static readonly TimeSpan FrugalRuntimeRefreshInterval = TimeSpan.FromSeconds(8);

        private readonly IDiagnosticsConsole _console;
        private readonly IConnectionService _connectionService;
        private readonly IDialogService _dialogService;
        private readonly IStringResources _strings;
        private readonly IHistoryService _history;

        private bool _isSessionLoggingEnabled;
        private bool _isVerboseLoggingEnabled;
        private string _logText;
        private string _runtimeHealthText;
        private string _runtimeExportStatusText;
        private string _buildInfoText;
        private string _socketSliceText;
        private bool _isActive;
        private readonly object _pendingLogLock = new object();
        private readonly StringBuilder _pendingLogLines = new StringBuilder();
        private readonly StringBuilder _socketSliceLines = new StringBuilder();
        private DateTimeOffset _lastAutoScrollUtc = DateTimeOffset.MinValue;

        public DebugViewModel(
            IDiagnosticsConsole console,
            IConnectionService connectionService,
            IDialogService dialogService,
            IStringResources strings,
            IHistoryService history)
        {
            _console = console;
            _connectionService = connectionService;
            _dialogService = dialogService;
            _strings = strings;
            _history = history;

            _isSessionLoggingEnabled = console.IsCaptureEnabled;
            _isVerboseLoggingEnabled = console.IsVerboseLoggingEnabled;
            _logText = TrimDisplayedLog(console.GetCapturedLog());
            _runtimeHealthText = _strings.Get("Debug_Collecting.Text", "Collecting runtime state...");
            _runtimeExportStatusText = string.Empty;
            _buildInfoText = "Build: ?";
            _socketSliceText = string.Empty;

            SaveLogCommand = new RelayCommand(async () => await _console.SaveCapturedLogAsync());
            ClearLogCommand = new RelayCommand(() =>
            {
                _console.ClearCapturedLog();
                LogText = string.Empty;
            });
            DeleteSessionCommand = new RelayCommand(async () => await ConfirmAndDeleteSessionAsync());
            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
            RefreshRuntimeCommand = new RelayCommand(() => RefreshRuntimeHealth(includeRecentEvents: true));
            SaveRuntimeReportCommand = new RelayCommand(async () => await SaveRuntimeReportAsync());
            ClearRuntimeLogCommand = new RelayCommand(async () => await ClearRuntimeLogAsync());
            RequestFullHistoryCommand = new RelayCommand(async () => await RequestFullHistoryAsync());
            RunSocketSliceCommand = new RelayCommand(async () => await RunSocketSliceAsync());
            StopSocketSliceCommand = new RelayCommand(async () => await StopSocketSliceAsync());

        }

        /// <summary>Raised when the user taps back on the debug surface.</summary>
        public event EventHandler BackRequested;

        /// <summary>Raised when the displayed session log text changes (view may auto-scroll).</summary>
        public event EventHandler LogTextChanged;

        /// <summary>
        /// True on Mobile / Low sync budget — live debug paint must stay cheap so Back stays
        /// responsive while catch-up and name refresh keep writing the journal.
        /// </summary>
        public bool PreferFrugalLivePaint =>
            _history != null && _history.PreferFrugalSyncBudget;

        /// <summary>Interval the view should use for <see cref="FlushPendingLogLines"/>.</summary>
        public TimeSpan LogFlushInterval =>
            PreferFrugalLivePaint ? FrugalLogFlushInterval : GenerousLogFlushInterval;

        /// <summary>Interval the view should use for periodic <see cref="RefreshRuntimeHealth"/>.</summary>
        public TimeSpan RuntimeRefreshInterval =>
            PreferFrugalLivePaint
                ? FrugalRuntimeRefreshInterval
                : GenerousRuntimeRefreshInterval;

        /// <summary>Bind toggles / start timers when the debug pane becomes visible.</summary>
        public void Activate(string buildInfoText = null)
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            if (!string.IsNullOrEmpty(buildInfoText))
            {
                BuildInfoText = buildInfoText;
            }

            RefreshFromServices();
            _console.LogLineAppended -= Console_LogLineAppended;
            _console.LogLineAppended += Console_LogLineAppended;

            // These two used to be hooked in the constructor and never dropped. The console is
            // a singleton and this ViewModel is transient, so each visit to the debug pane left
            // another listener behind - and the QR one opens a fullscreen dialog, so running a
            // socket slice after a few visits opened one dialog per abandoned instance.
            _console.SocketSliceReported -= Console_SocketSliceReported;
            _console.SocketSliceReported += Console_SocketSliceReported;
            _console.SocketSliceQrReceived -= Console_SocketSliceQrReceived;
            _console.SocketSliceQrReceived += Console_SocketSliceQrReceived;
            RefreshRuntimeHealth(includeRecentEvents: true);
        }

        /// <summary>Stop live subscriptions when the pane is hidden.</summary>
        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            _console.LogLineAppended -= Console_LogLineAppended;
            _console.SocketSliceReported -= Console_SocketSliceReported;
            _console.SocketSliceQrReceived -= Console_SocketSliceQrReceived;
            lock (_pendingLogLock)
            {
                _pendingLogLines.Clear();
            }
        }

        /// <summary>Refresh toggle/log snapshot from services when the pane opens.</summary>
        public void RefreshFromServices()
        {
            IsSessionLoggingEnabled = _console.IsCaptureEnabled;
            IsVerboseLoggingEnabled = _console.IsVerboseLoggingEnabled;
            LogText = TrimDisplayedLog(_console.GetCapturedLog());
        }

        /// <summary>Drain buffered live log lines onto <see cref="LogText"/> (view timer).</summary>
        public void FlushPendingLogLines()
        {
            string chunk;
            lock (_pendingLogLock)
            {
                if (_pendingLogLines.Length == 0)
                {
                    return;
                }

                chunk = _pendingLogLines.ToString();
                _pendingLogLines.Clear();
            }

            // Rebuild from the pending tail only when the display is already near the cap —
            // concatenating 60k + storm chunk then Substring is a GC spike on ARM.
            int maxChars = MaxLogCharacters;
            string current = LogText ?? string.Empty;
            if (current.Length >= maxChars && chunk.Length >= maxChars / 4)
            {
                LogText = TrimDisplayedLog(chunk);
                return;
            }

            LogText = TrimDisplayedLog(current + chunk);
        }

        /// <summary>
        /// True when the view should auto-scroll after a log paint. Frugal hosts throttle so
        /// ChangeView does not fight Back taps during journal storms.
        /// </summary>
        public bool ShouldAutoScrollLog()
        {
            if (!PreferFrugalLivePaint)
            {
                return true;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((now - _lastAutoScrollUtc) < FrugalLogFlushInterval)
            {
                return false;
            }

            _lastAutoScrollUtc = now;
            return true;
        }

        public bool IsSessionLoggingEnabled
        {
            get => _isSessionLoggingEnabled;
            set
            {
                if (Set(ref _isSessionLoggingEnabled, value))
                {
                    _console.IsCaptureEnabled = value;
                }
            }
        }

        public bool IsVerboseLoggingEnabled
        {
            get => _isVerboseLoggingEnabled;
            set
            {
                if (Set(ref _isVerboseLoggingEnabled, value))
                {
                    _console.SetVerboseLogging(value, nameof(DebugViewModel));
                }
            }
        }

        public string LogText
        {
            get => _logText;
            private set
            {
                if (Set(ref _logText, value))
                {
                    LogTextChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public string RuntimeHealthText
        {
            get => _runtimeHealthText;
            private set => Set(ref _runtimeHealthText, value);
        }

        public string RuntimeExportStatusText
        {
            get => _runtimeExportStatusText;
            private set => Set(ref _runtimeExportStatusText, value);
        }

        public string BuildInfoText
        {
            get => _buildInfoText;
            private set => Set(ref _buildInfoText, value);
        }

        /// <summary>Persists the in-memory session log buffer to a file.</summary>
        public ICommand SaveLogCommand { get; }

        /// <summary>Clears the session log buffer and the bound display text.</summary>
        public ICommand ClearLogCommand { get; }

        /// <summary>Confirms, then deletes local auth/session and returns to pairing.</summary>
        public ICommand DeleteSessionCommand { get; }

        /// <summary>Leaves the debug pane (raises <see cref="BackRequested"/>).</summary>
        public ICommand BackCommand { get; }

        /// <summary>Refreshes the runtime health snapshot text immediately.</summary>
        public ICommand RefreshRuntimeCommand { get; }

        /// <summary>Exports a diagnostic report and shows the result path/status.</summary>
        public ICommand SaveRuntimeReportCommand { get; }

        /// <summary>Clears the runtime diagnostics journal and refreshes the UI.</summary>
        public ICommand ClearRuntimeLogCommand { get; }

        /// <summary>Asks the phone for FULL_HISTORY_SYNC_ON_DEMAND (no wipe).</summary>
        public ICommand RequestFullHistoryCommand { get; }

        /// <summary>
        /// Runs the new Unison.Socket stack against the real servers on throwaway credentials.
        /// Nothing in the app depends on the result; this only proves the rewrite works.
        /// </summary>
        public ICommand RunSocketSliceCommand { get; }

        /// <summary>Tears down the probe connection.</summary>
        public ICommand StopSocketSliceCommand { get; }

        /// <summary>Hides the whole section on builds where the probe was not registered.</summary>
        public bool IsSocketSliceAvailable => _console.IsSocketSliceAvailable;

        public string SocketSliceText
        {
            get => _socketSliceText;
            private set => Set(ref _socketSliceText, value);
        }

        /// <summary>Alias used by older code-behind paths.</summary>
        public Task WipeSessionAsync() => ConfirmAndDeleteSessionAsync();

        private async Task RunSocketSliceAsync()
        {
            if (_console.IsSocketSliceRunning)
            {
                return;
            }

            _socketSliceLines.Clear();
            SocketSliceText = string.Empty;

            await _console.RunSocketSliceAsync();
        }

        private Task StopSocketSliceAsync()
        {
            return _console.StopSocketSliceAsync();
        }

        private void Console_SocketSliceReported(object sender, string line)
        {
            _socketSliceLines.AppendLine(line ?? string.Empty);
            SocketSliceText = _socketSliceLines.ToString();
        }

        private async void Console_SocketSliceQrReceived(object sender, string qr)
        {
            try
            {
                await _dialogService.ShowQrFullscreenAsync(qr);
            }
            catch (Exception ex)
            {
                Console_SocketSliceReported(this, "Could not show QR: " + ex.Message);
            }
        }

        private void Console_LogLineAppended(object sender, string line)
        {
            int maxChars = MaxLogCharacters;
            lock (_pendingLogLock)
            {
                _pendingLogLines.AppendLine(line ?? string.Empty);
                if (_pendingLogLines.Length > maxChars)
                {
                    string keep = _pendingLogLines.ToString();
                    _pendingLogLines.Clear();
                    _pendingLogLines.Append(keep.Substring(keep.Length - maxChars));
                }
            }
        }

        /// <summary>
        /// Rebuilds the runtime health text. Periodic timer on frugal skips the recent-events
        /// dump (largest string); manual refresh and Activate keep the full report.
        /// </summary>
        public void RefreshRuntimeHealth() => RefreshRuntimeHealth(includeRecentEvents: !PreferFrugalLivePaint);

        public void RefreshRuntimeHealth(bool includeRecentEvents)
        {
            try
            {
                RuntimeDiagnosticsSnapshot snapshot = _console.CaptureRuntimeSnapshot();
                if (!includeRecentEvents)
                {
                    RuntimeHealthText = snapshot.ToDisplayText();
                    return;
                }

                string recent = _console.GetRecentRuntimeText();
                RuntimeHealthText = snapshot.ToDisplayText() +
                    Environment.NewLine +
                    "RECENT RUNTIME EVENTS" + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(recent) ? "<none>" : recent);
            }
            catch (Exception ex)
            {
                RuntimeHealthText = "Unable to capture runtime health: " + ex.Message;
            }
        }

        private int MaxLogCharacters =>
            PreferFrugalLivePaint ? FrugalMaxDisplayedLogCharacters : MaxDisplayedLogCharacters;

        private async Task SaveRuntimeReportAsync()
        {
            RuntimeExportStatusText = _strings.Get("Debug_PreparingReport", "Preparing report…");
            string result = await _console.ExportRuntimeReportAsync();
            RuntimeExportStatusText = result;
            RefreshRuntimeHealth(includeRecentEvents: true);
        }

        private async Task ClearRuntimeLogAsync()
        {
            await _console.ClearRuntimeLogAsync();
            RuntimeExportStatusText = _strings.Get("Debug_RuntimeCleared", "Runtime log cleared.");
            RefreshRuntimeHealth(includeRecentEvents: true);
        }

        private async Task RequestFullHistoryAsync()
        {
            if (_history == null)
            {
                RuntimeExportStatusText = _strings.Get(
                    "Debug_FullHistoryUnavailable",
                    "History service unavailable.");
                return;
            }

            RuntimeExportStatusText = _strings.Get(
                "Debug_FullHistoryRequesting",
                "Requesting full history…");
            try
            {
                bool ok = await _history.RequestFullHistoryOnDemandAsync(
                    "debug-view",
                    force: true).ConfigureAwait(false);
                RuntimeExportStatusText = ok
                    ? _strings.Get("Debug_FullHistoryRequested", "Full history on-demand requested.")
                    : _strings.Get(
                        "Debug_FullHistorySkipped",
                        "Full history request was not sent (socket not ready or skipped).");
            }
            catch (Exception ex)
            {
                RuntimeExportStatusText = _strings.Get(
                    "Debug_FullHistoryFailed",
                    "Full history request failed: ") + ex.Message;
            }

            RefreshRuntimeHealth(includeRecentEvents: true);
        }

        private async Task ConfirmAndDeleteSessionAsync()
        {
            bool confirmed = await _dialogService.ShowConfirmAsync(
                title: _strings.Get("Debug_WipeTitle"),
                content: _strings.Get("Debug_WipeBody"),
                primaryButtonText: _strings.Get("Debug_WipeDelete"),
                closeButtonText: _strings.Get("Debug_WipeCancel"));

            if (confirmed)
            {
                await _connectionService.ClearLocalSessionAsync();
            }
        }

        private string TrimDisplayedLog(string text)
        {
            int maxChars = MaxLogCharacters;
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            {
                return text ?? string.Empty;
            }

            return text.Substring(text.Length - maxChars);
        }
    }
}
