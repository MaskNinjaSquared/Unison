using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// App settings (notifications / live tiles / keep-alive / shell / language) + about credits.
    /// Mirrors Imgur SettingsViewModel: toggles write LocalSettingsConstants and
    /// notify platform services.
    /// </summary>
    public class SettingsViewModel : Observable
    {
        private readonly ILocalSettings _localSettings;
        private readonly ILiveTilesService _liveTilesService;
        private readonly INotificationService _notificationService;
        private readonly ILocationKeepAliveService _locationKeepAlive;
        private readonly IShellThemeService _shellTheme;
        private readonly IAppLanguageService _appLanguage;
        private readonly IStringResources _strings;
        private readonly IDialogService _dialogService;
        private readonly IWhatsAppService _whatsAppService;
        private readonly ShellViewModel _shell;

        private string _appVersion;
        private bool _keepAliveBusy;
        private bool _shellChangeBusy;
        private bool _languageChangeBusy;
        private bool _disconnectBusy;

        public SettingsViewModel(
            ILocalSettings localSettings,
            ILiveTilesService liveTilesService,
            INotificationService notificationService,
            ILocationKeepAliveService locationKeepAlive,
            IShellThemeService shellTheme,
            IAppLanguageService appLanguage,
            IStringResources strings,
            IDialogService dialogService,
            IWhatsAppService whatsAppService,
            ShellViewModel shell)
        {
            _localSettings = localSettings;
            _liveTilesService = liveTilesService;
            _notificationService = notificationService;
            _locationKeepAlive = locationKeepAlive;
            _shellTheme = shellTheme;
            _appLanguage = appLanguage;
            _strings = strings;
            _dialogService = dialogService;
            _whatsAppService = whatsAppService;
            _shell = shell;

            _shell.PropertyChanged += OnShellPropertyChanged;

            LeaveCommand = new RelayCommand(() => LeaveRequested?.Invoke(this, EventArgs.Empty));
            ChangeShellCommand = new RelayCommand<int>(index =>
            {
                if (_shellChangeBusy)
                {
                    return;
                }

                _ = ChangeShellAsync(index);
            });
            ChangeLanguageCommand = new RelayCommand<int>(index =>
            {
                if (_languageChangeBusy)
                {
                    return;
                }

                _ = ChangeLanguageAsync(index);
            });
            DisconnectCommand = new RelayCommand(() =>
            {
                if (_disconnectBusy)
                {
                    return;
                }

                _ = ConfirmAndDisconnectAsync();
            });
        }

        public event EventHandler LeaveRequested;

        /// <summary>Signed-in display name (from shell profile).</summary>
        public string ProfileDisplayName => _shell.ProfileDisplayName;

        /// <summary>Signed-in avatar URL (from shell profile).</summary>
        public string CurrentUserAvatar => _shell.CurrentUserAvatar;

        public bool NotificationsEnabled
        {
            get => _localSettings.Get<bool>(LocalSettingsConstants.NotificationsEnabled);
            set
            {
                _localSettings.Set(LocalSettingsConstants.NotificationsEnabled, value);
                _notificationService.OnNotificationsConfigChanged();
                OnPropertyChanged();
            }
        }

        public bool LiveTilesEnabled
        {
            get => _localSettings.Get<bool>(LocalSettingsConstants.LiveTilesEnabled);
            set
            {
                _localSettings.Set(LocalSettingsConstants.LiveTilesEnabled, value);
                _ = _liveTilesService.OnLiveTilesConfigChangedAsync();
                OnPropertyChanged();
            }
        }

        public bool LocationKeepAliveEnabled
        {
            get => _localSettings.Get<bool>(LocalSettingsConstants.LocationKeepAliveEnabled);
            set
            {
                if (_keepAliveBusy)
                {
                    OnPropertyChanged();
                    return;
                }

                _ = SetLocationKeepAliveAsync(value);
            }
        }

        /// <summary>
        /// When on, revoked/logged-out sessions clear local auth and show QR.
        /// Off keeps trying to reconnect (safer against false positives).
        /// </summary>
        public bool AutoUnlinkOnLogoutEnabled
        {
            get => _localSettings.Get<bool>(LocalSettingsConstants.AutoUnlinkOnLogoutEnabled);
            set
            {
                _localSettings.Set(LocalSettingsConstants.AutoUnlinkOnLogoutEnabled, value);
                OnPropertyChanged();
            }
        }

        /// <summary>Current persisted shell (for ComboBox OneWay display).</summary>
        public AppShell SelectedShell
        {
            get
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.SelectedShell);
                return Enum.IsDefined(typeof(AppShell), raw)
                    ? (AppShell)raw
                    : AppShell.Unison;
            }
        }

        /// <summary>ComboBox SelectedIndex (OneWay). Changes go through <see cref="ChangeShellCommand"/>.</summary>
        public int SelectedShellIndex => (int)SelectedShell;

        /// <summary>Current persisted UI language.</summary>
        public AppLanguage SelectedLanguage
        {
            get
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.SelectedLanguage);
                return AppLanguageInfo.FromStored(raw);
            }
        }

        /// <summary>ComboBox SelectedIndex into <see cref="LanguageOptions"/>.</summary>
        public int SelectedLanguageIndex
        {
            get
            {
                AppLanguage selected = SelectedLanguage;
                IReadOnlyList<AppLanguage> all = AppLanguageInfo.All;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == selected)
                    {
                        return i;
                    }
                }

                return 0;
            }
        }

        /// <summary>Native display names for the language ComboBox (order = <see cref="AppLanguageInfo.All"/>).</summary>
        public IReadOnlyList<string> LanguageOptions => AppLanguageInfo.GetDisplayNames(_strings);

        /// <summary>
        /// Display string for ComboBox SelectedItem. Prefer over SelectedIndex alone:
        /// UWP often applies SelectedIndex before ItemsSource and leaves -1 with OneWay.
        /// </summary>
        public string SelectedLanguageOption
        {
            get
            {
                IReadOnlyList<string> options = LanguageOptions;
                int index = SelectedLanguageIndex;
                if (options.Count == 0)
                {
                    return string.Empty;
                }

                if (index < 0 || index >= options.Count)
                {
                    return options[0];
                }

                return options[index];
            }
        }

        public string AppTitle => "Unison";

        public string AppDescription =>
            _strings.Get("Settings_AppDescription.Text",
                "WhatsApp Client based on Baileys for Windows Universal Platform.");

        public string AppBranch =>
            _strings.Get("Settings_AppBranch.Text", "Release");

        public string AppVersion
        {
            get => _appVersion;
            private set => Set(ref _appVersion, value);
        }

        public string DeveloperPrimary => "@MaskNinjaSquared";

        public string Contributors =>
            "@MayconUs" + Environment.NewLine +
            "@Negociation (Thiago Araujo)" + Environment.NewLine +
            "@jjb-pro";

        /// <summary>Leaves Settings and returns to the chats section.</summary>
        public ICommand LeaveCommand { get; }

        /// <summary>Applies a new app shell theme. Parameter: 0 = Unison, 1 = WhatsApp.</summary>
        public ICommand ChangeShellCommand { get; }

        /// <summary>Changes UI language and restarts. Parameter: index into <see cref="LanguageOptions"/>.</summary>
        public ICommand ChangeLanguageCommand { get; }

        /// <summary>Confirms, then wipes local auth/session and returns to pairing.</summary>
        public ICommand DisconnectCommand { get; }

        public void Initialize(string appVersion)
        {
            AppVersion = appVersion ?? string.Empty;
            RaiseToggleSettingsChanged();
            RaiseShellSelectionChanged();
            RaiseLanguageSelectionChanged();
            RaiseAboutCopyChanged();
            RaiseProfileHeaderChanged();
        }

        private void OnShellPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUserName) ||
                e.PropertyName == nameof(ShellViewModel.ProfileDisplayName) ||
                e.PropertyName == nameof(ShellViewModel.CurrentUserAvatar))
            {
                RaiseProfileHeaderChanged();
            }
        }

        private void RaiseToggleSettingsChanged()
        {
            RaiseProperties(
                nameof(NotificationsEnabled),
                nameof(LiveTilesEnabled),
                nameof(LocationKeepAliveEnabled),
                nameof(AutoUnlinkOnLogoutEnabled));
        }

        private void RaiseShellSelectionChanged()
        {
            RaiseProperties(nameof(SelectedShell), nameof(SelectedShellIndex));
        }

        private void RaiseLanguageSelectionChanged()
        {
            RaiseProperties(
                nameof(SelectedLanguage),
                // Items first, then selection — avoids ComboBox blank SelectedIndex race.
                nameof(LanguageOptions),
                nameof(SelectedLanguageIndex),
                nameof(SelectedLanguageOption));
        }

        private void RaiseAboutCopyChanged()
        {
            RaiseProperties(
                nameof(AppTitle),
                nameof(AppDescription),
                nameof(AppBranch),
                nameof(DeveloperPrimary),
                nameof(Contributors));
        }

        private void RaiseProfileHeaderChanged()
        {
            RaiseProperties(nameof(ProfileDisplayName), nameof(CurrentUserAvatar));
        }

        private async Task ConfirmAndDisconnectAsync()
        {
            _disconnectBusy = true;
            try
            {
                bool confirmed = await _dialogService.ShowConfirmAsync(
                    title: _strings.Get("Settings_DisconnectTitle", "Disconnect?"),
                    content: _strings.Get(
                        "Settings_DisconnectBody",
                        "If you disconnect, you will be taken to the pairing screen and all data and chats will be erased."),
                    primaryButtonText: _strings.Get("Settings_DisconnectConfirm", "Disconnect"),
                    closeButtonText: _strings.Get("Settings_DisconnectCancel", "Cancel"));

                if (confirmed)
                {
                    await _whatsAppService.ClearSessionAsync();
                }
            }
            finally
            {
                _disconnectBusy = false;
            }
        }

        private async Task ChangeShellAsync(int index)
        {
            if (!Enum.IsDefined(typeof(AppShell), index))
            {
                return;
            }

            var shell = (AppShell)index;
            if (shell == SelectedShell)
            {
                return;
            }

            _shellChangeBusy = true;
            try
            {
                await _shellTheme.ChangeShellAndRestartAsync(shell);
            }
            finally
            {
                _shellChangeBusy = false;
                RaiseShellSelectionChanged();
            }
        }

        private async Task ChangeLanguageAsync(int index)
        {
            IReadOnlyList<AppLanguage> all = AppLanguageInfo.All;
            if (index < 0 || index >= all.Count)
            {
                return;
            }

            AppLanguage language = all[index];
            // Same as saved → ignore. Override staleness is healed by ApplyFromSettings on launch,
            // not by ComboBox rebinds (those were toast+Exit looping on Mobile).
            if (language == SelectedLanguage)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                "[SettingsViewModel] ChangeLanguage index=" + index + " → " + language +
                " tag=" + AppLanguageInfo.GetTag(language));

            _languageChangeBusy = true;
            try
            {
                await _appLanguage.ChangeLanguageAndRestartAsync(language);
            }
            finally
            {
                _languageChangeBusy = false;
                RaiseLanguageSelectionChanged();
            }
        }

        private async Task SetLocationKeepAliveAsync(bool enabled)
        {
            _keepAliveBusy = true;
            try
            {
                if (enabled)
                {
                    _localSettings.Set(LocalSettingsConstants.LocationKeepAliveEnabled, true);
                    RaiseLocationKeepAliveChanged();

                    bool ok = await _locationKeepAlive.StartAsync();
                    if (!ok)
                    {
                        _localSettings.Set(LocalSettingsConstants.LocationKeepAliveEnabled, false);
                        RaiseLocationKeepAliveChanged();
                    }
                }
                else
                {
                    _localSettings.Set(LocalSettingsConstants.LocationKeepAliveEnabled, false);
                    RaiseLocationKeepAliveChanged();
                    _locationKeepAlive.Stop();
                }
            }
            finally
            {
                _keepAliveBusy = false;
            }
        }

        private void RaiseLocationKeepAliveChanged() =>
            OnPropertyChanged(nameof(LocationKeepAliveEnabled));
    }
}
