using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
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

        /// <summary>Owns leaving the account: unlink on the server, then wipe locally.</summary>
        private readonly IConnectionService _connectionService;
        private readonly IContactService _contacts;

        private readonly ShellViewModel _shell;

        private string _appVersion;
        private bool _keepAliveBusy;
        private bool _shellChangeBusy;
        private bool _languageChangeBusy;
        private bool _timeFormatChangeBusy;
        private bool _disconnectBusy;
        private bool _publishContactsBusy;

        public SettingsViewModel(
            ILocalSettings localSettings,
            ILiveTilesService liveTilesService,
            INotificationService notificationService,
            ILocationKeepAliveService locationKeepAlive,
            IShellThemeService shellTheme,
            IAppLanguageService appLanguage,
            IStringResources strings,
            IDialogService dialogService,
            IConnectionService connectionService,
            ShellViewModel shell,
            IContactService contacts = null)
        {
            _localSettings = localSettings;
            _liveTilesService = liveTilesService;
            _notificationService = notificationService;
            _locationKeepAlive = locationKeepAlive;
            _shellTheme = shellTheme;
            _appLanguage = appLanguage;
            _strings = strings;
            _dialogService = dialogService;
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _shell = shell;
            _contacts = contacts;

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
            ChangeTimeFormatCommand = new RelayCommand<int>(index =>
            {
                if (_timeFormatChangeBusy)
                {
                    return;
                }

                ChangeTimeFormat(index);
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

        /// <summary>
        /// Copies 1:1 Unison chats into a separate Windows People list. Off by default.
        /// </summary>
        public bool PublishContactsToWindowsEnabled
        {
            get => _localSettings.Get<bool>(LocalSettingsConstants.PublishContactsToWindowsEnabled);
            set
            {
                if (_publishContactsBusy)
                {
                    OnPropertyChanged();
                    return;
                }

                _ = SetPublishContactsToWindowsAsync(value);
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

        /// <summary>Current persisted clock (<see cref="TimeFormat"/>).</summary>
        public TimeFormat SelectedTimeFormat
        {
            get
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.TimeFormat);
                return Enum.IsDefined(typeof(TimeFormat), raw)
                    ? (TimeFormat)raw
                    : TimeFormat.Hours24;
            }
        }

        /// <summary>ComboBox SelectedIndex (OneWay). Changes go through <see cref="ChangeTimeFormatCommand"/>.</summary>
        public int SelectedTimeFormatIndex => (int)SelectedTimeFormat;

        public string AppTitle => "Unison";

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

        /// <summary>Applies 24h or 12h clock. Parameter: 0 = 24 Hours, 1 = 12 Hours.</summary>
        public ICommand ChangeTimeFormatCommand { get; }

        /// <summary>Confirms, then wipes local auth/session and returns to pairing.</summary>
        public ICommand DisconnectCommand { get; }

        public void Initialize(string appVersion)
        {
            AppVersion = appVersion ?? string.Empty;
            WhatsAppMapper.CurrentTimeFormat = SelectedTimeFormat;
            RaiseToggleSettingsChanged();
            RaiseShellSelectionChanged();
            RaiseLanguageSelectionChanged();
            RaiseTimeFormatSelectionChanged();
            RaiseAboutCopyChanged();
            RaiseProfileHeaderChanged();
        }

        private void OnShellPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUserName) ||
                e.PropertyName == nameof(ShellViewModel.CurrentUserPhone) ||
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
                nameof(AutoUnlinkOnLogoutEnabled),
                nameof(PublishContactsToWindowsEnabled));
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
                nameof(DeveloperPrimary),
                nameof(Contributors));
        }

        private void RaiseProfileHeaderChanged()
        {
            RaiseProperties(nameof(ProfileDisplayName), nameof(CurrentUserAvatar));
        }

        private void RaiseTimeFormatSelectionChanged()
        {
            RaiseProperties(nameof(SelectedTimeFormat), nameof(SelectedTimeFormatIndex));
        }

        private void ChangeTimeFormat(int index)
        {
            if (!Enum.IsDefined(typeof(TimeFormat), index))
            {
                return;
            }

            var format = (TimeFormat)index;
            if (format == SelectedTimeFormat)
            {
                return;
            }

            _timeFormatChangeBusy = true;
            try
            {
                _localSettings.Set(LocalSettingsConstants.TimeFormat, (int)format);
                WhatsAppMapper.CurrentTimeFormat = format;
            }
            finally
            {
                _timeFormatChangeBusy = false;
                RaiseTimeFormatSelectionChanged();
            }
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
                    // Through the facade, so the account is told we are leaving before the keys
                    // that would prove who is leaving are gone.
                    await _connectionService.LogoutAsync("settings");
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

        private async Task SetPublishContactsToWindowsAsync(bool enabled)
        {
            _publishContactsBusy = true;
            try
            {
                if (_contacts != null)
                {
                    await _contacts.SetPublishContactsToWindowsAsync(enabled);
                }
                else
                {
                    _localSettings.Set(LocalSettingsConstants.PublishContactsToWindowsEnabled, enabled);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SettingsViewModel] Publish contacts to Windows failed: " + ex.Message);
            }
            finally
            {
                _publishContactsBusy = false;
                OnPropertyChanged(nameof(PublishContactsToWindowsEnabled));
            }
        }
    }
}
