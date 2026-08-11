using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Services.Themes;
using Windows.ApplicationModel.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Swaps Themes/{Shell}/Theme.xaml and dispatches chrome/sync policy via
    /// <see cref="ShellThemeStrategy"/> (Unison vs WhatsApp).
    /// </summary>
    public sealed class ShellThemeService : IShellThemeService
    {
        private readonly ILocalSettings _localSettings;
        private readonly IDialogService _dialogs;
        private readonly IStringResources _strings;
        private readonly ISystemInfoProvider _systemInfo;
        private ShellThemeStrategy _strategy;

        public ShellThemeService(
            ILocalSettings localSettings,
            IDialogService dialogs,
            IStringResources strings,
            ISystemInfoProvider systemInfo)
        {
            _localSettings = localSettings;
            _dialogs = dialogs;
            _strings = strings;
            _systemInfo = systemInfo;
            _strategy = CreateStrategy(ReadSelectedShell());
        }

        public bool DisplaySyncInChatList => Current.DisplaySyncInChatList;

        public bool UsesMobileStatusBarProgress => Current.UsesMobileStatusBarProgress;

        private ShellThemeStrategy Current =>
            _strategy ?? (_strategy = CreateStrategy(ReadSelectedShell()));

        public void ApplyFromSettings()
        {
            AppShell shell = ReadSelectedShell();
            _strategy = CreateStrategy(shell);
            ApplyTheme(shell);
            ApplyChrome();
            MaybeShowPendingRestartToast();
        }

        public void ApplyChrome()
        {
            try
            {
                Current.SetTitleBar();
                _ = Current.SetMobileStatusBarAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] ApplyChrome: " + ex.Message);
            }
        }

        public Task ApplyMobileStatusBarAsync()
        {
            try
            {
                return Current.SetMobileStatusBarAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] ApplyMobileStatusBar: " + ex.Message);
                return Task.CompletedTask;
            }
        }

        public async Task ChangeShellAndRestartAsync(AppShell shell)
        {
            var current = ReadSelectedShell();
            if (shell == current)
            {
                return;
            }

            _localSettings.Set(LocalSettingsConstants.SelectedShell, (int)shell);
            _localSettings.Set(LocalSettingsConstants.PendingShellAppliedToast, true);

            string title = _strings.Get("Settings_ShellRestartTitle");
            string body = _strings.Get("Settings_ShellRestartBody");
            string close = _strings.Get("Settings_ShellRestartClose");
            if (string.IsNullOrWhiteSpace(title) || title.StartsWith("Settings_", StringComparison.Ordinal))
            {
                title = "Shell updated";
            }

            if (string.IsNullOrWhiteSpace(body) || body.StartsWith("Settings_", StringComparison.Ordinal))
            {
                body = "Unison will restart to apply the new shell.";
            }

            if (string.IsNullOrWhiteSpace(close) || close.StartsWith("Settings_", StringComparison.Ordinal))
            {
                close = "OK";
            }

            try
            {
                await _dialogs.ShowMessageAsync(title, body, close);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] dialog failed: " + ex.Message);
            }

            try
            {
                // Returns only on failure; on success the process is replaced.
                var reason = await CoreApplication.RequestRestartAsync(string.Empty);
                Debug.WriteLine("[ShellTheme] RequestRestartAsync failed: " + reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] RequestRestartAsync exception: " + ex.Message);
            }

            try
            {
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] Exit failed: " + ex.Message);
            }
        }

        private ShellThemeStrategy CreateStrategy(AppShell shell)
        {
            if (shell == AppShell.WhatsApp)
            {
                return new WhatsAppThemeStrategy();
            }

            return new UnisonThemeStrategy(_systemInfo);
        }

        private AppShell ReadSelectedShell()
        {
            try
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.SelectedShell);
                return Enum.IsDefined(typeof(AppShell), raw) ? (AppShell)raw : AppShell.Unison;
            }
            catch
            {
                return AppShell.Unison;
            }
        }

        private static void ApplyTheme(AppShell shell)
        {
            try
            {
                string folder = shell == AppShell.WhatsApp ? "WhatsApp" : "Unison";
                var uri = new Uri("ms-appx:///Themes/" + folder + "/Theme.xaml");

                var merges = Application.Current.Resources.MergedDictionaries;
                for (int i = merges.Count - 1; i >= 0; i--)
                {
                    var src = merges[i].Source;
                    if (src == null)
                    {
                        continue;
                    }

                    string path = src.OriginalString ?? string.Empty;
                    if (path.IndexOf("/Themes/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (path.EndsWith("Theme.xaml", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("Styles.xaml", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("Controls.xaml", StringComparison.OrdinalIgnoreCase) ||
                         path.EndsWith("SystemAccentOverrides.xaml", StringComparison.OrdinalIgnoreCase)))
                    {
                        merges.RemoveAt(i);
                    }
                }

                merges.Add(new ResourceDictionary { Source = uri });
                Debug.WriteLine("[ShellTheme] applied " + folder);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] ApplyTheme failed: " + ex.Message);
            }
        }

        private void MaybeShowPendingRestartToast()
        {
            try
            {
                if (!_localSettings.Get<bool>(LocalSettingsConstants.PendingShellAppliedToast))
                {
                    return;
                }

                _localSettings.Set(LocalSettingsConstants.PendingShellAppliedToast, false);

                string title = _strings.Get("Settings_ShellAppliedTitle");
                string body = _strings.Get("Settings_ShellAppliedBody");
                if (string.IsNullOrWhiteSpace(title) || title.StartsWith("Settings_", StringComparison.Ordinal))
                {
                    title = "Shell applied";
                }

                if (string.IsNullOrWhiteSpace(body) || body.StartsWith("Settings_", StringComparison.Ordinal))
                {
                    body = "Your selected shell is now active.";
                }

                string xml =
                    "<toast><visual><binding template=\"ToastGeneric\">" +
                    "<text>" + EscapeXml(title) + "</text>" +
                    "<text>" + EscapeXml(body) + "</text>" +
                    "</binding></visual></toast>";

                var doc = new XmlDocument();
                doc.LoadXml(xml);
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(doc));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ShellTheme] pending toast failed: " + ex.Message);
            }
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
