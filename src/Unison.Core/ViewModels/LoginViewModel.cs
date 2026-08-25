using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Pairing surface (QR + link-with-phone). Platform draws the QR bitmap;
    /// dialogs go through <see cref="IDialogService"/> with this VM as target.
    /// </summary>
    public class LoginViewModel : Observable
    {
        /// <summary>How long we wait for OnQRCodeReceived after ConnectAsync.</summary>
        private const int QrWaitTimeoutMs = 45000;

        private readonly IConnectionService _connection;
        private readonly IDispatcher _dispatcher;
        private readonly ISessionLogger _sessionLogger;
        private readonly IDialogService _dialogService;
        private readonly IStringResources _strings;

        private bool _isLoading;
        private bool _hasError;
        private string _errorMessage;
        private string _qrData;
        private string _statusText;
        private TaskCompletionSource<string> _qrWaitTcs;
        private readonly RelayCommand _showQrFullscreenCommand;
        private bool _isLogPanelVisible;
        private string _diagnosticLogText;
        private string _versionText;
        private bool _logLiveHooked;
        private bool _connectionHooked;

        public LoginViewModel(
            IConnectionService connection,
            IDispatcher dispatcher,
            ISessionLogger sessionLogger,
            IDialogService dialogService,
            IStringResources strings)
        {
            _connection = connection;
            _dispatcher = dispatcher;
            _sessionLogger = sessionLogger;
            _dialogService = dialogService;
            _strings = strings;

            StartPairingCommand = new RelayCommand(async () => await StartPairingFlowAsync());
            LinkWithPhoneCommand = new RelayCommand(async () => await LinkWithPhoneAsync());
            _showQrFullscreenCommand = new RelayCommand(
                async () => await ShowQrFullscreenAsync(),
                () => !string.IsNullOrEmpty(QRData) && !HasError);
            ShowQrFullscreenCommand = _showQrFullscreenCommand;
            ToggleLogPanelCommand = new RelayCommand(ToggleLogPanel);
            ToggleSessionLoggingCommand = new RelayCommand(ToggleSessionLogging);
            DevResetSessionCommand = new RelayCommand(async () => await ResetSessionForDevAsync());

            _versionText = "v?";

            Attach();
        }

        /// <summary>
        /// Starts listening to the pairing facade. Called from the constructor so a screen that
        /// is still being built does not miss the first code, and again by the view when it is
        /// re-attached to the tree. Doing it twice is harmless.
        /// </summary>
        public void Attach()
        {
            if (_connectionHooked || _connection == null)
            {
                return;
            }

            _connection.QrReceived += Connection_QrReceived;
            _connection.QrExpired += Connection_QrExpired;
            _connection.StatusChanged += Connection_StatusChanged;
            _connection.Failed += Connection_Failed;
            _connectionHooked = true;
        }

        /// <summary>
        /// Stops listening. The facade outlives this view model, so without it every login screen
        /// ever shown would stay alive on the end of these events, redrawing a UI nobody sees.
        /// </summary>
        public void Detach()
        {
            if (!_connectionHooked || _connection == null)
            {
                return;
            }

            _connection.QrReceived -= Connection_QrReceived;
            _connection.QrExpired -= Connection_QrExpired;
            _connection.StatusChanged -= Connection_StatusChanged;
            _connection.Failed -= Connection_Failed;
            _connectionHooked = false;
        }

        private void Connection_QrReceived(object sender, string qrData)
        {
            int len = qrData?.Length ?? 0;
            _sessionLogger.WriteAlways($"[Pairing] QR received len={len}");
            _ = _dispatcher.RunAsync(() =>
            {
                QRData = qrData;
                StatusText = Loc("Login_StatusQrReceived", "QR received — generating image…");
                _qrWaitTcs?.TrySetResult(qrData);
            });
        }

        private void Connection_QrExpired(object sender, EventArgs e)
        {
            _sessionLogger.WriteAlways("[Pairing] QR expired");
            _ = _dispatcher.RunAsync(() =>
            {
                // Dropping the payload is what disables the fullscreen preview and lets the
                // status text through again; the error is what raises the reload button.
                QRData = null;
                IsLoading = false;
                HasError = true;
                ErrorMessage = Loc(
                    "Login_StatusQrExpired",
                    "QR code expired - tap Reload QR to get a new one.");
                StatusText = ErrorMessage;

                // Completing rather than cancelling: a pairing flow still waiting for its first
                // QR should stop waiting, but it must not surface a cancellation on top of the
                // message that already explains what happened.
                _qrWaitTcs?.TrySetResult(null);
            });
        }

        private void Connection_StatusChanged(object sender, string status)
        {
            string value = status ?? string.Empty;
            _sessionLogger.WriteAlways($"[Pairing] connection={value}");
            _ = _dispatcher.RunAsync(() =>
            {
                if (string.IsNullOrEmpty(QRData))
                {
                    StatusText = DescribeConnectionStatus(value);
                }
            });
        }

        private void Connection_Failed(object sender, Exception ex)
        {
            _sessionLogger.WriteErrorAlways("[Pairing] connection failed", ex);
            _ = _dispatcher.RunAsync(() =>
            {
                if (string.IsNullOrEmpty(QRData))
                {
                    HasError = true;
                    ErrorMessage = ex?.Message ?? Loc("Login_UnknownError", "unknown error");
                    StatusText = Loc("Login_StatusErrorPrefix", "Error: ") + ErrorMessage;
                }
            });
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        public bool HasError
        {
            get => _hasError;
            private set
            {
                if (Set(ref _hasError, value))
                {
                    RaiseQrFullscreenCanExecuteChanged();
                }
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set => Set(ref _errorMessage, value);
        }

        public string QRData
        {
            get => _qrData;
            private set
            {
                if (Set(ref _qrData, value))
                {
                    RaiseQrFullscreenCanExecuteChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public string VersionText
        {
            get => _versionText;
            set => Set(ref _versionText, value);
        }

        public bool IsLogPanelVisible
        {
            get => _isLogPanelVisible;
            private set => Set(ref _isLogPanelVisible, value);
        }

        public string DiagnosticLogText
        {
            get => _diagnosticLogText;
            private set => Set(ref _diagnosticLogText, value);
        }

        /// <summary>Whether session logging is on (drives Enable/Disable log button Visibility).</summary>
        public bool IsSessionLoggingEnabled => _sessionLogger != null && _sessionLogger.Enabled;

        /// <summary>Connects and waits for a QR payload (or times out).</summary>
        public ICommand StartPairingCommand { get; }

        /// <summary>Prompts for phone number and shows the WhatsApp pairing code dialog.</summary>
        public ICommand LinkWithPhoneCommand { get; }

        /// <summary>Opens the current QR payload in a fullscreen preview dialog.</summary>
        public ICommand ShowQrFullscreenCommand { get; }

        /// <summary>Shows or hides the full-screen diagnostic log overlay.</summary>
        public ICommand ToggleLogPanelCommand { get; }

        /// <summary>Enables or disables session log capture while the overlay is open.</summary>
        public ICommand ToggleSessionLoggingCommand { get; }

        /// <summary>Dev-only: wipe local session after the instruction header 5-tap gesture.</summary>
        public ICommand DevResetSessionCommand { get; }

        private async Task ShowQrFullscreenAsync()
        {
            if (string.IsNullOrEmpty(QRData) || HasError)
            {
                return;
            }

            await _dialogService.ShowQrFullscreenAsync(QRData);
        }

        public async Task ResetSessionForDevAsync()
        {
            await _connection.ClearLocalSessionAsync();
            await _dialogService.ShowMessageAsync(
                _strings.Get("Login_DevResetTitle"),
                _strings.Get("Login_DevResetBody"),
                _strings.Get("Common_OK"));
        }

        public void DeactivateDiagnostics()
        {
            UnhookLiveLog();
            IsLogPanelVisible = false;
        }

        private void ToggleLogPanel()
        {
            if (IsLogPanelVisible)
            {
                UnhookLiveLog();
                IsLogPanelVisible = false;
                return;
            }

            try
            {
                RefreshDiagnosticLogText();
                HookLiveLog();
            }
            catch (Exception ex)
            {
                DiagnosticLogText = string.Format(
                    _strings.Get("Login_LogReadFail", "Failed to read log: {0}"),
                    ex.Message);
            }

            IsLogPanelVisible = true;
        }

        private void ToggleSessionLogging()
        {
            try
            {
                bool enabled = !_sessionLogger.Enabled;
                _sessionLogger.Enabled = enabled;
                OnPropertyChanged(nameof(IsSessionLoggingEnabled));
                DiagnosticLogText = enabled
                    ? _strings.Get("Login_LogEnabledHint", "Logging enabled.")
                    : _strings.Get("Login_LogDisabledHint", "Logging disabled.");
            }
            catch (Exception ex)
            {
                DiagnosticLogText = string.Format(
                    _strings.Get("Login_LogToggleFail", "Failed to toggle log: {0}"),
                    ex.Message);
            }
        }

        private void HookLiveLog()
        {
            if (_logLiveHooked)
            {
                return;
            }

            _sessionLogger.OnLogUpdated += SessionLogger_OnLogUpdated;
            _logLiveHooked = true;
        }

        private void UnhookLiveLog()
        {
            if (!_logLiveHooked)
            {
                return;
            }

            _sessionLogger.OnLogUpdated -= SessionLogger_OnLogUpdated;
            _logLiveHooked = false;
        }

        private void SessionLogger_OnLogUpdated(object sender, string line)
        {
            _ = _dispatcher.RunAsync(RefreshDiagnosticLogText);
        }

        private void RefreshDiagnosticLogText()
        {
            try
            {
                string text = _sessionLogger.GetLogText();
                DiagnosticLogText = string.IsNullOrWhiteSpace(text)
                    ? _strings.Get("Login_LogEmpty", "(empty)")
                    : text;
            }
            catch (Exception ex)
            {
                DiagnosticLogText = string.Format(
                    _strings.Get("Login_LogReadFail", "Failed to read log: {0}"),
                    ex.Message);
            }
        }

        private void RaiseQrFullscreenCanExecuteChanged() =>
            _showQrFullscreenCommand?.RaiseCanExecuteChanged();

        public async Task StartPairingFlowAsync()
        {
            _sessionLogger.PairingTraceActive = true;
            var qrWait = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _qrWaitTcs = qrWait;

            try
            {
                IsLoading = true;
                HasError = false;
                ErrorMessage = null;
                QRData = null;
                StatusText = Loc("Login_StatusStartingPairing", "Starting pairing…");
                _sessionLogger.WriteAlways("[Pairing] StartPairingFlow begin");
                _sessionLogger.WriteAlways(
                    "[Pairing] Nota: FileNotFoundException first-chance no Output costuma ser " +
                    "probe de recurso UWP (benigno). EndOfStream no Background.winmd ao " +
                    "reclaim do socket tambem e tipico.");

                StatusText = Loc("Login_StatusConnecting", "Connecting to WhatsApp…");
                await _connection.StartPairingAsync();
                _sessionLogger.WriteAlways(
                    "[Pairing] StartPairing returned; status=" +
                    (_connection.CurrentStatus ?? "(null)"));

                if (!string.IsNullOrEmpty(QRData))
                {
                    _sessionLogger.WriteAlways("[Pairing] QR already present after ConnectAsync");
                    return;
                }

                StatusText = Loc("Login_StatusWaitingQr", "Waiting for QR from server…");
                var finished = await Task.WhenAny(qrWait.Task, Task.Delay(QrWaitTimeoutMs));
                if (finished != qrWait.Task)
                {
                    HasError = true;
                    string timeout = Loc("Login_StatusQrTimeout", "Timeout waiting for QR ({0}s) — tap Reload QR and open the log.")
                        .Replace("{0}", (QrWaitTimeoutMs / 1000).ToString());
                    ErrorMessage = timeout;
                    StatusText = timeout;
                    _sessionLogger.WriteAlways(
                        "[Pairing] TIMEOUT waiting for QR. Last connection status=" +
                        (_connection.CurrentStatus ?? "(null)"));
                    IsLoading = false;
                    return;
                }

                await qrWait.Task;
                _sessionLogger.WriteAlways("[Pairing] QR wait completed");
            }
            catch (Exception ex)
            {
                _sessionLogger.WriteErrorAlways("[Pairing] StartPairingFlow failed", ex);
                HasError = true;
                ErrorMessage = ex.Message;
                StatusText = Loc("Login_StatusFailPrefix", "Failed: ") + ex.GetType().Name + " — " + ex.Message;
                IsLoading = false;
            }
        }

        public void OnQRDisplayed()
        {
            IsLoading = false;
            HasError = false;
            StatusText = Loc("Login_QRStatus.Text", "Scan this code with your phone");
            _sessionLogger.WriteAlways("[Pairing] QR bitmap displayed on UI");
        }

        public void OnQRDisplayFailed(Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = ex?.Message ?? Loc("Login_StatusDrawFailedFallback", "failed to draw QR");
            StatusText = Loc("Login_StatusDrawFailedPrefix", "Failed to draw QR: ") + ErrorMessage;
            _sessionLogger.WriteErrorAlways("[Pairing] DisplayQRCode failed", ex);
        }

        public async Task LinkWithPhoneAsync()
        {
            try
            {
                string raw = await _dialogService.ShowInputAsync(
                    title: _strings.Get("Login_PairPhoneTitle"),
                    prompt: _strings.Get("Login_PairPhonePrompt"),
                    placeholder: "5562999999999",
                    primaryButtonText: _strings.Get("Login_PairPhoneContinue"),
                    closeButtonText: _strings.Get("Login_PairPhoneCancel"));

                if (raw == null)
                {
                    return;
                }

                string phone = PhoneNumberHelper.NormalizePhoneDigits(raw);
                if (string.IsNullOrEmpty(phone) || phone.Length < 10)
                {
                    await _dialogService.ShowMessageAsync(
                        _strings.Get("Login_PairPhoneInvalidTitle"),
                        _strings.Get("Login_PairPhoneInvalidBody"),
                        _strings.Get("Common_OK"));
                    return;
                }

                IsLoading = true;
                _sessionLogger.PairingTraceActive = true;
                _sessionLogger.WriteAlways("[Pairing] phone pairing start phoneLen=" + phone.Length);

                string code = await _connection.RequestPairingCodeAsync(phone);
                if (string.IsNullOrEmpty(code))
                {
                    IsLoading = false;
                    _sessionLogger.WriteAlways("[Pairing] phone pairing unavailable");
                    await _dialogService.ShowMessageAsync(
                        _strings.Get("Login_PairPhoneNoConnTitle"),
                        _strings.Get("Login_PairPhoneNoConnBody"),
                        _strings.Get("Common_OK"));
                    return;
                }

                IsLoading = false;
                _sessionLogger.WriteAlways(
                    "[Pairing] phone pairing code received len=" + (code?.Length ?? 0));

                await _dialogService.ShowPairingCodeAsync(this, code);
            }
            catch (Exception ex)
            {
                IsLoading = false;
                _sessionLogger.WriteErrorAlways("[Pairing] phone pairing failed", ex);
                await _dialogService.ShowMessageAsync(
                    _strings.Get("Login_PairPhoneFailTitle"),
                    ex.Message ?? string.Empty,
                    _strings.Get("Common_OK"));
            }
        }

        private string Loc(string key, string fallback)
        {
            return _strings != null ? _strings.Get(key, fallback) : fallback;
        }

        private string DescribeConnectionStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return Loc("Login_Conn_Empty", "Connected");
            }

            switch (status.Trim().ToLowerInvariant())
            {
                case "connecting":
                    return Loc("Login_Conn_Connecting", "Opening a WebSocket…");
                case "connected":
                    return Loc("Login_Conn_Connected", "WebSocket connected — handshake…");
                case "open":
                    return Loc("Login_Conn_Open", "Handshake OK — waiting for pair-device…");
                case "disconnected":
                    return Loc("Login_Conn_Disconnected", "Disconnected — retrying…");
                case "close":
                    return Loc("Login_Conn_Close", "Connection closed");
                case "restart":
                    return Loc("Login_Conn_Restart", "Restarting pairing…");
                default:
                    return Loc("Login_Conn_Default", "Status: ") + status;
            }
        }
    }
}
