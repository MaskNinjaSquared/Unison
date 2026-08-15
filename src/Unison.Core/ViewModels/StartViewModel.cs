using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    /// <summary>Welcome / Get started — only when logged out, before Login/QR.</summary>
    public class StartViewModel : Observable
    {
        private readonly ShellViewModel _shell;
        private readonly ILocalSettings _localSettings;
        private readonly IAppLanguageService _appLanguage;
        private readonly IStringResources _strings;
        private bool _languageChangeBusy;

        public StartViewModel(
            ShellViewModel shell,
            ILocalSettings localSettings,
            IAppLanguageService appLanguage,
            IStringResources strings)
        {
            _shell = shell;
            _localSettings = localSettings;
            _appLanguage = appLanguage;
            _strings = strings;
            GetStartedCommand = new RelayCommand(GetStarted);
            ChangeLanguageCommand = new RelayCommand<int>(index =>
            {
                if (_languageChangeBusy)
                {
                    return;
                }

                _ = ChangeLanguageAsync(index);
            });
        }

        /// <summary>Navigates from the welcome screen into the login / QR surface.</summary>
        public ICommand GetStartedCommand { get; }

        /// <summary>Changes UI language and restarts. Parameter: index into <see cref="LanguageOptions"/>.</summary>
        public ICommand ChangeLanguageCommand { get; }

        public string AppVersion { get; set; }

        public AppLanguage SelectedLanguage
        {
            get
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.SelectedLanguage);
                return AppLanguageInfo.FromStored(raw);
            }
        }

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

        /// <summary>Refresh language ComboBox bindings after DataContext is assigned.</summary>
        public void RefreshLanguageSelection()
        {
            RaiseLanguageSelectionChanged();
        }

        private void RaiseLanguageSelectionChanged()
        {
            RaiseProperties(
                nameof(SelectedLanguage),
                nameof(LanguageOptions),
                nameof(SelectedLanguageIndex),
                nameof(SelectedLanguageOption));
        }

        private void GetStarted()
        {
            _shell.EnterLoginSurface(startPairing: true);
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
                "[StartViewModel] ChangeLanguage index=" + index + " → " + language +
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
    }
}
