using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Debug / diagnostics pane (session log, verbose toggle, wipe session).
    /// Confirm dialogs use <see cref="IDialogService"/> (Imgur pattern).
    /// </summary>
    public class DebugViewModel : Observable
    {
        // â”€â”€ DI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>WhatsApp session (verbose flag + ClearSession).</summary>
        private readonly IWhatsAppService _whatsAppService;

        /// <summary>Persistent session log text / save / clear.</summary>
        private readonly ISessionLogger _sessionLogger;

        /// <summary>Confirm wipe and other platform dialogs.</summary>
        private readonly IDialogService _dialogService;

        /// <summary>Localized wipe dialog strings.</summary>
        private readonly IStringResources _strings;

        // â”€â”€ State â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private bool _isSessionLoggingEnabled;
        private bool _isVerboseLoggingEnabled;
        private string _logText;

        public DebugViewModel(
            IWhatsAppService whatsAppService,
            ISessionLogger sessionLogger,
            IDispatcher dispatcher,
            IDialogService dialogService,
            IStringResources strings)
        {
            _whatsAppService = whatsAppService;
            _sessionLogger = sessionLogger;
            _dialogService = dialogService;
            _strings = strings;

            _isSessionLoggingEnabled = sessionLogger.Enabled;
            _isVerboseLoggingEnabled = whatsAppService.VerboseLogging;
            _logText = sessionLogger.GetLogText();

            // Persist current log buffer to a file.
            SaveLogCommand = new RelayCommand(async () => await _sessionLogger.SaveToFileAsync());

            // Clear in-memory log and the bound text.
            ClearLogCommand = new RelayCommand(() =>
            {
                _sessionLogger.Clear();
                LogText = string.Empty;
            });

            // Confirm then wipe auth/session (returns to pairing).
            DeleteSessionCommand = new RelayCommand(async () => await ConfirmAndDeleteSessionAsync());

            // Leave debug and return to chats (shell listens to BackRequested).
            BackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        // â”€â”€ Events â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Raised when the user taps back on the debug surface.</summary>
        public event EventHandler BackRequested;

        // â”€â”€ Lifecycle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Refresh toggle/log snapshot from services when the pane opens.</summary>
        public void RefreshFromServices()
        {
            IsSessionLoggingEnabled = _sessionLogger.Enabled;
            IsVerboseLoggingEnabled = _whatsAppService.VerboseLogging;
            LogText = _sessionLogger.GetLogText();
        }

        /// <summary>Append one live log line (view forwards SessionLogger updates).</summary>
        public void AppendLogLine(string entry)
        {
            LogText += (entry ?? string.Empty) + "\n";
        }

        // â”€â”€ Bindable state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Persist session log to LocalFolder when enabled.</summary>
        public bool IsSessionLoggingEnabled
        {
            get => _isSessionLoggingEnabled;
            set
            {
                if (Set(ref _isSessionLoggingEnabled, value))
                    _sessionLogger.Enabled = value;
            }
        }

        /// <summary>Verbose protocol / Baileys logging on WhatsAppService.</summary>
        public bool IsVerboseLoggingEnabled
        {
            get => _isVerboseLoggingEnabled;
            set
            {
                if (Set(ref _isVerboseLoggingEnabled, value))
                    _whatsAppService.SetVerboseLogging(value, nameof(DebugViewModel));
            }
        }

        /// <summary>Full log text shown in the debug TextBox.</summary>
        public string LogText
        {
            get => _logText;
            private set => Set(ref _logText, value);
        }

        // â”€â”€ Commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>Save log buffer to a file via ISessionLogger.</summary>
        public ICommand SaveLogCommand { get; }

        /// <summary>Clear the session log buffer and UI text.</summary>
        public ICommand ClearLogCommand { get; }

        /// <summary>Confirm and delete the WhatsApp session (pairing required again).</summary>
        public ICommand DeleteSessionCommand { get; }

        /// <summary>Leave the debug pane.</summary>
        public ICommand BackCommand { get; }

        /// <summary>Alias used by older code-behind paths.</summary>
        public Task WipeSessionAsync() => ConfirmAndDeleteSessionAsync();

        // â”€â”€ Private â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
    }
}
