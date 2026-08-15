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
        private readonly IDiagnosticsConsole _console;
        private readonly IDialogService _dialogService;
        private readonly IStringResources _strings;

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

        public DebugViewModel(
            IWhatsAppService whatsAppService,
            IDiagnosticsConsole console,
            IDialogService dialogService,
            IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _console = console;
            _dialogService = dialogService;
            _strings = strings;

            _isSessionLoggingEnabled = console.IsCaptureEnabled;
            _isVerboseLoggingEnabled = whatsAppService.VerboseLogging;
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
            RefreshRuntimeCommand = new RelayCommand(RefreshRuntimeHealth);
            SaveRuntimeReportCommand = new RelayCommand(async () => await SaveRuntimeReportAsync());
            ClearRuntimeLogCommand = new RelayCommand(async () => await ClearRuntimeLogAsync());
            RunSocketSliceCommand = new RelayCommand(async () => await RunSocketSliceAsync());
            StopSocketSliceCommand = new RelayCommand(async () => await StopSocketSliceAsync());

            _console.SocketSliceReported += Console_SocketSliceReported;
            _console.SocketSliceQrReceived += Console_SocketSliceQrReceived;
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
            _console.LogLineAppended -= Console_LogLineAppended;
            _console.LogLineAppended += Console_LogLineAppended;
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
            _console.LogLineAppended -= Console_LogLineAppended;
            lock (_pendingLogLock)
            {
                _pendingLogLines.Clear();
            }
        }

        /// <summary>Refresh toggle/log snapshot from services when the pane opens.</summary>
        public void RefreshFromServices()
        {
            IsSessionLoggingEnabled = _console.IsCaptureEnabled;
            IsVerboseLoggingEnabled = _whatsAppService.VerboseLogging;
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

            LogText = TrimDisplayedLog((LogText ?? string.Empty) + chunk);
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
                RuntimeDiagnosticsSnapshot snapshot = _console.CaptureRuntimeSnapshot();
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

        private async Task SaveRuntimeReportAsync()
        {
            RuntimeExportStatusText = _strings.Get("Debug_PreparingReport", "Preparing report…");
            string result = await _console.ExportRuntimeReportAsync();
            RuntimeExportStatusText = result;
            RefreshRuntimeHealth();
        }

        private async Task ClearRuntimeLogAsync()
        {
            await _console.ClearRuntimeLogAsync();
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
