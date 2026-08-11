using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// App settings (notifications / live tiles / keep-alive / shell) + about credits.
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
        private readonly IStringResources _strings;

        private string _appVersion;
        private bool _keepAliveBusy;
        private bool _shellChangeBusy;

        public SettingsViewModel(
            ILocalSettings localSettings,
            ILiveTilesService liveTilesService,
            INotificationService notificationService,
            ILocationKeepAliveService locationKeepAlive,
            IShellThemeService shellTheme,
            IStringResources strings)
        {
            _localSettings = localSettings;
            _liveTilesService = liveTilesService;
            _notificationService = notificationService;
            _locationKeepAlive = locationKeepAlive;
            _shellTheme = shellTheme;
            _strings = strings;

            LeaveCommand = new RelayCommand(() => LeaveRequested?.Invoke(this, EventArgs.Empty));
            ChangeShellCommand = new RelayCommand<int>(index =>
            {
                if (_shellChangeBusy)
                {
                    return;
                }

                _ = ChangeShellAsync(index);
            });
        }

        public event EventHandler LeaveRequested;

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
        public string Contributors => "@Maykon_Us, @Negociation (Thiago Araujo), @jjb-pro";

        public ICommand LeaveCommand { get; }

        /// <summary>Parameter: new shell index (0 Unison / 1 WhatsApp).</summary>
        public ICommand ChangeShellCommand { get; }

        public void Initialize(string appVersion)
        {
            AppVersion = appVersion ?? string.Empty;
            OnPropertyChanged(nameof(NotificationsEnabled));
            OnPropertyChanged(nameof(LiveTilesEnabled));
            OnPropertyChanged(nameof(LocationKeepAliveEnabled));
            OnPropertyChanged(nameof(SelectedShell));
            OnPropertyChanged(nameof(SelectedShellIndex));
            OnPropertyChanged(nameof(AppTitle));
            OnPropertyChanged(nameof(AppDescription));
            OnPropertyChanged(nameof(AppBranch));
            OnPropertyChanged(nameof(DeveloperPrimary));
            OnPropertyChanged(nameof(Contributors));
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
                OnPropertyChanged(nameof(SelectedShell));
                OnPropertyChanged(nameof(SelectedShellIndex));
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
                    OnPropertyChanged(nameof(LocationKeepAliveEnabled));

                    bool ok = await _locationKeepAlive.StartAsync();
                    if (!ok)
                    {
                        _localSettings.Set(LocalSettingsConstants.LocationKeepAliveEnabled, false);
                        OnPropertyChanged(nameof(LocationKeepAliveEnabled));
                    }
                }
                else
                {
                    _localSettings.Set(LocalSettingsConstants.LocationKeepAliveEnabled, false);
                    OnPropertyChanged(nameof(LocationKeepAliveEnabled));
                    _locationKeepAlive.Stop();
                }
            }
            finally
            {
                _keepAliveBusy = false;
            }
        }
    }
}
