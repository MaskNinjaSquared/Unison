using System.Windows.Input;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.State;

namespace Unison.Core.ViewModels
{
    /// <summary>
    /// Legacy settings: the same toggles, plus a category the page shows or hides.
    /// Back closes that category before it leaves settings.
    /// </summary>
    public class LegacySettingsViewModel : SettingsViewModel
    {
        public const string Profile = "profile";
        public const string General = "general";
        public const string Notifications = "notifications";
        public const string Customization = "customization";
        public const string Account = "account";

        private readonly SettingsDrillIn _drill = new SettingsDrillIn();

        public LegacySettingsViewModel(
            ILocalSettings localSettings,
            ILiveTilesService liveTilesService,
            INotificationService notificationService,
            ILocationKeepAliveService locationKeepAlive,
            IShellThemeService shellTheme,
            IAppLanguageService appLanguage,
            IStringResources strings,
            IDialogService dialogService,
            IStartTilePinService startTiles,
            IMediaDiskUsageService diskUsage,
            IConnectionService connectionService,
            ShellViewModel shell,
            IContactService contacts = null,
            SyncStatusChannel syncStatus = null)
            : base(
                localSettings,
                liveTilesService,
                notificationService,
                locationKeepAlive,
                shellTheme,
                appLanguage,
                strings,
                dialogService,
                startTiles,
                diskUsage,
                connectionService,
                shell,
                contacts,
                syncStatus)
        {
            OpenCategoryCommand = new RelayCommand<string>(OpenCategory);
        }

        public ICommand OpenCategoryCommand { get; }

        public string OpenCategoryId => _drill.OpenId;

        public bool IsCategoryRoot => _drill.IsRoot;

        public void OpenCategory(string id)
        {
            _drill.Open(id);
            RaiseCategory();
        }

        /// <summary>True when a category was showing and the page should stay on settings.</summary>
        public bool TryCloseCategory()
        {
            if (!_drill.TryClose())
            {
                return false;
            }

            RaiseCategory();
            return true;
        }

        private void RaiseCategory()
        {
            RaiseProperties(nameof(OpenCategoryId), nameof(IsCategoryRoot));
        }
    }
}
