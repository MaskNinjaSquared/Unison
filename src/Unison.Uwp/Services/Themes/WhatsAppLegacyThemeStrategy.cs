using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// WhatsApp Legacy shell: Windows Phone 8–style pivot host (no sidebar).
    /// Root is <see cref="Shell.WhatsAppLegacy.Views.ShellView"/>; settings navigate in the shell frame.
    /// </summary>
    public sealed class WhatsAppLegacyThemeStrategy : ShellThemeStrategy
    {
        private static readonly Color WpBlack = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        private static readonly Color WpWhite = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly Color WpAccent = Color.FromArgb(0xFF, 0x00, 0x65, 0x6A);

        public override bool DisplaySyncInChatList => true;

        public override bool UsesMobileStatusBarProgress => false;

        public override string ShellBrandName => "WhatsApp Legacy";

        public override string ShellBrandIcon =>
            "ms-appx:///Assets/CustomTiles/WhatsAppLegacy.png";

        public override Type ResolveRootPage(NavigationDestination destination)
        {
            if (destination == NavigationDestination.AppShell)
            {
                return typeof(Shell.WhatsAppLegacy.Views.ShellView);
            }

            return base.ResolveRootPage(destination);
        }

        public override Type ResolveShellPage(NavigationDestination destination)
        {
            switch (destination)
            {
                case NavigationDestination.Chats:
                case NavigationDestination.Archived:
                case NavigationDestination.Status:
                    throw new InvalidOperationException(
                        "WhatsApp Legacy hosts chats/status/archived in the shell pivot, not the overlay frame.");
                case NavigationDestination.Debug:
                    return typeof(Shell.WhatsAppLegacy.Views.DebugView);
                default:
                    return base.ResolveShellPage(destination);
            }
        }

        public override bool TryResolveShellDestination(object content, out NavigationDestination destination)
        {
            if (content is Shell.WhatsAppLegacy.Views.ChatsView)
            {
                destination = NavigationDestination.Chats;
                return true;
            }

            if (content is Shell.WhatsAppLegacy.Views.ArchivedChatsView)
            {
                destination = NavigationDestination.Archived;
                return true;
            }

            if (content is Shell.WhatsAppLegacy.Views.StatusView)
            {
                destination = NavigationDestination.Status;
                return true;
            }

            if (content is Shell.WhatsAppLegacy.Views.DebugView)
            {
                destination = NavigationDestination.Debug;
                return true;
            }

            return base.TryResolveShellDestination(content, out destination);
        }

        public override void SetTitleBar()
        {
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                // Desktop: accent strip (#00656A); caption glyphs stay white.
                titleBar.BackgroundColor = WpAccent;
                titleBar.InactiveBackgroundColor = WpAccent;
                titleBar.ForegroundColor = WpWhite;
                titleBar.InactiveForegroundColor = WpWhite;
                titleBar.ButtonBackgroundColor = WpAccent;
                titleBar.ButtonInactiveBackgroundColor = WpAccent;
                titleBar.ButtonForegroundColor = WpWhite;
                titleBar.ButtonInactiveForegroundColor = WpWhite;
                titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0xFF, 0x00, 0x7A, 0x80);
                titleBar.ButtonHoverForegroundColor = WpWhite;
                titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0xFF, 0x00, 0x52, 0x56);
                titleBar.ButtonPressedForegroundColor = WpWhite;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppLegacyTheme] SetTitleBar: " + ex.Message);
            }
        }

        public override async Task SetMobileStatusBarAsync()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
                {
                    return;
                }

                var statusBar = StatusBar.GetForCurrentView();
                // No brand strip on Mobile — black status bar only (WP8 chrome lives in-page).
                statusBar.BackgroundColor = WpBlack;
                statusBar.ForegroundColor = WpWhite;
                statusBar.BackgroundOpacity = 1.0;
                try { await statusBar.ProgressIndicator.HideAsync(); } catch { }
                await statusBar.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppLegacyTheme] SetMobileStatusBar: " + ex.Message);
            }
        }

        public override void OpenSettings(Frame shellFrame, bool openProfile = false)
        {
            if (openProfile && shellFrame?.Content is Shell.WhatsAppLegacy.Views.SettingsView settings)
            {
                settings.OpenProfile();
                return;
            }

            NavigateShellFrame(shellFrame, typeof(Shell.WhatsAppLegacy.Views.SettingsView), openProfile);
        }

        public override bool IsSettingsPage(object content) =>
            content is Shell.WhatsAppLegacy.Views.SettingsView;
    }
}
