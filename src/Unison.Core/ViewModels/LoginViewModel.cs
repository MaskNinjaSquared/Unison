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

        /// <summary>WhatsApp session / connect / pairing handler.</summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>Marshals event handlers onto the UI thread.</summary>
        private readonly IDispatcher _dispatcher;

        /// <summary>Persistent / pairing diagnostic log.</summary>
        private readonly ISessionLogger _sessionLogger;

        /// <summary>Content dialogs (phone prompt, code display, errors).</summary>
        private readonly IDialogService _dialogService;

        /// <summary>Localized strings for pairing dialogs.</summary>
        private readonly IStringResources _strings;

        private bool _isLoading;
        private bool _hasError;
        private string _errorMessage;
        private string _qrData;
        private string _statusText;
        private TaskCompletionSource<string> _qrWaitTcs;

        public LoginViewModel(
            IWhatsAppService whatsAppService,
            IDispatcher dispatcher,
            ISessionLogger sessionLogger,
            IDialogService dialogService,
            IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _dispatcher = dispatcher;
            _sessionLogger = sessionLogger;
            _dialogService = dialogService;
            _strings = strings;

            // Start / retry QR pairing flow.
            StartPairingCommand = new RelayCommand(async () => await StartPairingFlowAsync());

            // Link device using a phone number + pairing code (instead of QR).
            LinkWithPhoneCommand = new RelayCommand(async () => await LinkWithPhoneAsync());

            _whatsAppService.OnQRCodeReceived += async (s, qrData) =>
            {
                int len = qrData?.Length ?? 0;
                _sessionLogger.WriteAlways($"[Pairing] OnQRCodeReceived len={len}");
                await _dispatcher.RunAsync(() =>
                {
                    QRData = qrData;
                    StatusText = "QR recebido â€” gerando imagemâ€¦";
                    _qrWaitTcs?.TrySetResult(qrData);
                });
            };

            _whatsAppService.OnConnectionUpdate += async (s, status) =>
            {
                string value = status ?? string.Empty;
                _sessionLogger.WriteAlways($"[Pairing] connection={value}");
                await _dispatcher.RunAsync(() =>
                {
                    if (string.IsNullOrEmpty(QRData))
                    {
                        StatusText = DescribeConnectionStatus(value);
                    }
                });
            };

            _whatsAppService.OnError += async (s, ex) =>
            {
                _sessionLogger.WriteErrorAlways("[Pairing] OnError", ex);
                await _dispatcher.RunAsync(() =>
                {
                    if (string.IsNullOrEmpty(QRData))
                    {
                        HasError = true;
                        ErrorMessage = ex?.Message ?? "erro desconhecido";
                        StatusText = "Erro: " + ErrorMessage;
                    }
                });
            };
        }

        /// <summary>True while connect / QR wait / phone pairing is in progress.</summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        /// <summary>True when the last pairing attempt failed (shows reload).</summary>
        public bool HasError
        {
            get => _hasError;
            private set => Set(ref _hasError, value);
        }

        /// <summary>Last error message for status / diagnostics.</summary>
        public string ErrorMessage
        {
            get => _errorMessage;
            private set => Set(ref _errorMessage, value);
        }

        /// <summary>Raw QR payload from the server (view renders bitmap).</summary>
        public string QRData
        {
            get => _qrData;
            private set => Set(ref _qrData, value);
        }

        /// <summary>Human-readable status under the QR area.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        /// <summary>Connect and wait for a QR code (or reload after timeout/error).</summary>
        public ICommand StartPairingCommand { get; }

        /// <summary>Prompt for phone number and show the pairing code dialog.</summary>
        public ICommand LinkWithPhoneCommand { get; }

        /// <summary>
        /// Hidden 5-tap reset on the instruction header: wipe local session and
        /// show a short confirmation (shell returns to pairing via OnSessionCleared).
        /// </summary>
        public async Task ResetSessionForDevAsync()
        {
            await _whatsAppService.ClearSessionAsync();
            await _dialogService.ShowMessageAsync(
                _strings.Get("Login_DevResetTitle"),
                _strings.Get("Login_DevResetBody"),
                _strings.Get("Common_OK"));
        }

        /// <summary>Connects, waits for QR (or times out). View still draws the bitmap.</summary>
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
                StatusText = "Iniciando pareamentoâ€¦";
                _sessionLogger.WriteAlways("[Pairing] StartPairingFlow begin");
                _sessionLogger.WriteAlways(
                    "[Pairing] Nota: FileNotFoundException first-chance no Output costuma ser " +
                    "probe de recurso UWP (benigno). EndOfStream no Background.winmd ao " +
                    "reclaim do socket tambem e tipico.");

                StatusText = "Conectando ao WhatsAppâ€¦";
                await _whatsAppService.ConnectAsync();
                _sessionLogger.WriteAlways(
                    "[Pairing] ConnectAsync returned; status=" +
                    (_whatsAppService.CurrentConnectionStatus ?? "(null)"));

                if (!string.IsNullOrEmpty(QRData))
                {
                    _sessionLogger.WriteAlways("[Pairing] QR already present after ConnectAsync");
                    return;
                }

                StatusText = "Aguardando QR do servidorâ€¦";
                var finished = await Task.WhenAny(qrWait.Task, Task.Delay(QrWaitTimeoutMs));
                if (finished != qrWait.Task)
                {
                    HasError = true;
                    ErrorMessage = "Timeout aguardando QR (" + (QrWaitTimeoutMs / 1000) + "s)";
                    StatusText = ErrorMessage + " â€” toque em Recarregar QR e abra o log.";
                    _sessionLogger.WriteAlways(
                        "[Pairing] TIMEOUT waiting for QR. Last connection status=" +
                        (_whatsAppService.CurrentConnectionStatus ?? "(null)"));
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
                StatusText = "Falha: " + ex.GetType().Name + " â€” " + ex.Message;
                IsLoading = false;
            }
        }

        /// <summary>Called by the view after ZXing successfully draws the QR image.</summary>
        public void OnQRDisplayed()
        {
            IsLoading = false;
            HasError = false;
            StatusText = "Escaneie este cÃ³digo com o telefone";
            _sessionLogger.WriteAlways("[Pairing] QR bitmap displayed on UI");
        }

        /// <summary>Called by the view when QR bitmap generation fails.</summary>
        public void OnQRDisplayFailed(Exception ex)
        {
            IsLoading = false;
            HasError = true;
            ErrorMessage = ex?.Message ?? "falha ao desenhar QR";
            StatusText = "Falha ao desenhar QR: " + ErrorMessage;
            _sessionLogger.WriteErrorAlways("[Pairing] DisplayQRCode failed", ex);
        }

        /// <summary>
        /// Asks for phone via DialogService, requests code from Pairing, shows it
        /// with ShowPairingCodeAsync(this, code).
        /// </summary>
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

                if (_whatsAppService.Pairing == null)
                {
                    await _whatsAppService.ConnectAsync();
                }

                var pairing = _whatsAppService.Pairing;
                if (pairing == null)
                {
                    IsLoading = false;
                    _sessionLogger.WriteAlways("[Pairing] phone pairing: Pairing handler null");
                    await _dialogService.ShowMessageAsync(
                        _strings.Get("Login_PairPhoneNoConnTitle"),
                        _strings.Get("Login_PairPhoneNoConnBody"),
                        _strings.Get("Common_OK"));
                    return;
                }

                string code = await pairing.RequestPairingCodeAsync(phone);
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

        // helpers
        private static string DescribeConnectionStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "Connected";
            }

            switch (status.Trim().ToLowerInvariant())
            {
                case "connecting":
                    return "Opening a WebSocket…";
                case "connected":
                    return "WebSocket connected — handshake…";
                case "open":
                    return "Handshake OK — waiting for pair-device…";
                case "disconnected":
                    return "Disconnected — retrying…";
                case "close":
                    return "Connection closed";
                case "restart":
                    return "Restarting pairing…";
                default:
                    return "Status: " + status;
            }
        }
    }
}
