using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Helpers;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel.Resources.Core;
using Windows.Data.Xml.Dom;
using Windows.Globalization;
using Windows.UI.Notifications;
using Windows.UI.Xaml;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Persists language via PrimaryLanguageOverride.
    /// Desktop: dialog + RequestRestartAsync + Exit.
    /// Mobile: toast to reopen + Exit (auto-restart unsupported). Next cold launch
    /// applies the saved setting before any UI is built.
    /// </summary>
    public sealed class AppLanguageService : IAppLanguageService
    {
        private readonly ILocalSettings _localSettings;
        private readonly IDialogService _dialogs;
        private readonly IStringResources _strings;
        private readonly ISystemInfoProvider _systemInfo;

        public AppLanguageService(
            ILocalSettings localSettings,
            IDialogService dialogs,
            IStringResources strings,
            ISystemInfoProvider systemInfo)
        {
            _localSettings = localSettings;
            _dialogs = dialogs;
            _strings = strings;
            _systemInfo = systemInfo;
        }

        public void ApplyFromSettings()
        {
            try
            {
                ApplyOverride(ReadSelectedLanguage());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] ApplyFromSettings: " + ex.Message);
            }
        }

        public async Task ChangeLanguageAndRestartAsync(AppLanguage language)
        {
            AppLanguage current = ReadSelectedLanguage();
            if (language == current)
            {
                return;
            }

            _localSettings.Set(LocalSettingsConstants.SelectedLanguage, (int)language);
            ApplyOverride(language);

            string title = _strings.Get("Settings_LanguageRestartTitle", "Language updated");
            string body = _strings.Get(
                "Settings_LanguageRestartBody",
                "Unison will restart to apply the new language.");
            string close = _strings.Get("Settings_LanguageRestartClose", "OK");

            await PromptRestartAndExitAsync(title, body, close);
        }

        /// <summary>
        /// Desktop: dialog + RequestRestartAsync + Exit.
        /// Mobile: reopen toast + short delay + Exit (no UI remount).
        /// </summary>
        private async Task PromptRestartAndExitAsync(string title, string body, string close)
        {
            bool mobile = _systemInfo != null && _systemInfo.IsMobile();
            if (mobile)
            {
                string toastTitle = _strings.Get("Settings_ReopenAppTitle", "Reopen Unison?");
                if (string.IsNullOrWhiteSpace(toastTitle) ||
                    toastTitle.StartsWith("Settings_", StringComparison.Ordinal))
                {
                    toastTitle = "Reopen Unison?";
                }

                ShowImmediateToast(toastTitle, body);

                try
                {
                    // Let the toast reach the notification center before the process dies.
                    await Task.Delay(450);
                }
                catch
                {
                }

                try
                {
                    Application.Current.Exit();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[AppLanguage] Mobile Exit failed: " + ex.Message);
                }

                return;
            }

            try
            {
                await _dialogs.ShowMessageAsync(title, body, close);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] dialog failed: " + ex.Message);
            }

            try
            {
                var reason = await CoreApplication.RequestRestartAsync(string.Empty);
                Debug.WriteLine("[AppLanguage] RequestRestartAsync → " + reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] RequestRestartAsync exception: " + ex.Message);
            }

            try
            {
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] Exit failed: " + ex.Message);
            }
        }

        private static void ShowImmediateToast(string title, string body)
        {
            try
            {
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
                Debug.WriteLine("[AppLanguage] toast failed: " + ex.Message);
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

        /// <summary>
        /// System → empty override when OS has a shipped locale (follow Languages).
        /// System + unsupported OS → force en-US. Concrete languages set their tag.
        /// Must run before any page is constructed so x:Uid uses the saved language.
        /// </summary>
        internal static void ApplyOverride(AppLanguage language)
        {
            string desired = ResolveOverrideTag(language);

            // Always assign — Mobile may keep a stale MRT context when skipping a no-op write.
            ApplicationLanguages.PrimaryLanguageOverride = desired;

            try
            {
                ResourceContext independent = ResourceContext.GetForViewIndependentUse();
                independent.Reset();
                if (!string.IsNullOrEmpty(desired))
                {
                    independent.Languages = new List<string> { desired };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] ResourceContext (independent): " + ex.Message);
            }

            try
            {
                ResourceContext view = ResourceContext.GetForCurrentView();
                view.Reset();
                if (!string.IsNullOrEmpty(desired))
                {
                    view.Languages = new List<string> { desired };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[AppLanguage] ResourceContext (view): " + ex.Message);
            }

            LocalizedStrings.Reset();

            // ManifestLanguages lists what MRT can actually resolve on this device. If a
            // shipped language is missing here the package/deploy dropped its resources.
            string manifest;
            try
            {
                manifest = string.Join(",", ApplicationLanguages.ManifestLanguages);
            }
            catch (Exception ex)
            {
                manifest = "<" + ex.Message + ">";
            }

            Debug.WriteLine(
                "[AppLanguage] ApplyOverride → '" + desired + "'; Primary='" +
                (ApplicationLanguages.PrimaryLanguageOverride ?? string.Empty) +
                "'; Manifest=" + manifest);
        }

        private static string ResolveOverrideTag(AppLanguage language)
        {
            if (!AppLanguageInfo.IsSystem(language))
            {
                return AppLanguageInfo.GetTag(language);
            }

            if (AppLanguageInfo.OsListContainsShipped(ApplicationLanguages.Languages))
            {
                return string.Empty;
            }

            return AppLanguageInfo.GetTag(AppLanguage.English);
        }

        private AppLanguage ReadSelectedLanguage()
        {
            try
            {
                int raw = _localSettings.Get<int>(LocalSettingsConstants.SelectedLanguage);
                return AppLanguageInfo.FromStored(raw);
            }
            catch
            {
                return AppLanguage.System;
            }
        }
    }
}
