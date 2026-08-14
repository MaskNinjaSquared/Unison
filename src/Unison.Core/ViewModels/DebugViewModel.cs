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

        private readonly IWhatsAppService _whatsAppService;
        private readonly ISessionLogger _sessionLogger;
        private readonly IDialogService _dialogService;
        private readonly IStringResources _strings;
        private readonly IRuntimeDiagnostics _runtimeDiagnostics;

        private bool _isSessionLoggingEnabled;
        private bool _isVerboseLoggingEnabled;
        private string _logText;
        private string _runtimeHealthText;
        private string _runtimeExportStatusText;
        private string _buildInfoText;
        private bool _isActive;
        private readonly object _pendingLogLock = new object();
        private readonly StringBuilder _pendingLogLines = new StringBuilder();

        public DebugViewModel(
            IWhatsAppService whatsAppService,
            ISessionLogger sessionLogger,
            IDialogService dialogService,
            IStringResources strings,
            IRuntimeDiagnostics runtimeDiagnostics)
        {
            _whatsAppService = whatsAppService;
            _sessionLogger = sessionLogger;
            _dialogService = dialogService;
            _strings = strings;
            _runtimeDiagnostics = runtimeDiagnostics;

            _isSessionLoggingEnabled = sessionLogger.Enabled;
            _isVerboseLoggingEnabled = whatsAppService.VerboseLogging;
            _logText = TrimDisplayedLog(sessionLogger.GetLogText());
            _runtimeHealthText = _strings.Get("Debug_Collecting.Text", "Collecting runtime state...");
            _runtimeExportStatusText = string.Empty;
            _buildInfoText = "Build: ?";

            SaveLogCommand = new RelayCommand(async () => await _sessionLogger.SaveToFileAsync());
            ClearLogCommand = new RelayCommand(() =>
            {
                _sessionLogger.Clear();
                LogText = string.Empty;
            });
            DeleteSessionCommand = new RelayCommand(async () => await ConfirmAndDeleteSessionAsync());
            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
            RefreshRuntimeCommand = new RelayCommand(RefreshRuntimeHealth);
            SaveRuntimeReportCommand = new RelayCommand(async () => await SaveRuntimeReportAsync());
            ClearRuntimeLogCommand = new RelayCommand(async () => await ClearRuntimeLogAsync());
        }

        /// <summary>Raised when the user taps back on the debug surface.</summary>
        public event EventHandler BackRequested;

        /// <summary>Raised when the displayed session log text changes (view may auto-scroll).</summary>
        public event EventHandler LogTextChanged;

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
            _sessionLogger.OnLogUpdated -= SessionLogger_OnLogUpdated;
            _sessionLogger.OnLogUpdated += SessionLogger_OnLogUpdated;
            RefreshRuntimeHealth();
        }

        /// <summary>Stop live subscriptions when the pane is hidden.</summary>
        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            _sessionLogger.OnLogUpdated -= SessionLogger_OnLogUpdated;
            lock (_pendingLogLock)
            {
                _pendingLogLines.Clear();
            }
        }

        /// <summary>Refresh toggle/log snapshot from services when the pane opens.</summary>
        public void RefreshFromServices()
        {
            IsSessionLoggingEnabled = _sessionLogger.Enabled;
            IsVerboseLoggingEnabled = _whatsAppService.VerboseLogging;
            LogText = TrimDisplayedLog(_sessionLogger.GetLogText());
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

            LogText = TrimDisplayedLog((LogText ?? string.Empty) + chunk);
        }

        public bool IsSessionLoggingEnabled
        {
            get => _isSessionLoggingEnabled;
            set
            {
                if (Set(ref _isSessionLoggingEnabled, value))
                {
                    _sessionLogger.Enabled = value;
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
                    _whatsAppService.SetVerboseLogging(value, nameof(DebugViewModel));
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

        /// <summary>Alias used by older code-behind paths.</summary>
        public Task WipeSessionAsync() => ConfirmAndDeleteSessionAsync();

        private void SessionLogger_OnLogUpdated(object sender, string line)
        {
            lock (_pendingLogLock)
            {
                _pendingLogLines.AppendLine(line ?? string.Empty);
                if (_pendingLogLines.Length > MaxDisplayedLogCharacters)
                {
                    string keep = _pendingLogLines.ToString();
                    _pendingLogLines.Clear();
                    _pendingLogLines.Append(keep.Substring(keep.Length - MaxDisplayedLogCharacters));
                }
            }
        }

        public void RefreshRuntimeHealth()
        {
            try
            {
                RuntimeDiagnosticsSnapshot snapshot = _runtimeDiagnostics.CaptureSnapshot();
                string recent = _runtimeDiagnostics.GetRecentText();
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

        private async Task SaveRuntimeReportAsync()
        {
            RuntimeExportStatusText = _strings.Get("Debug_PreparingReport", "Preparing report…");
            string result = await _runtimeDiagnostics.ExportReportAsync();
            RuntimeExportStatusText = result;
            RefreshRuntimeHealth();
        }

        private async Task ClearRuntimeLogAsync()
        {
            await _runtimeDiagnostics.ClearAsync();
            RuntimeExportStatusText = _strings.Get("Debug_RuntimeCleared", "Runtime log cleared.");
            RefreshRuntimeHealth();
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
                await _whatsAppService.ClearSessionAsync();
            }
        }

        private static string TrimDisplayedLog(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxDisplayedLogCharacters)
            {
                return text ?? string.Empty;
            }

            return text.Substring(text.Length - MaxDisplayedLogCharacters);
        }
    }
}
