using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// Unison chrome: green title bar on PC; green "Unison" status bar on Mobile portrait.
    /// Sync progress uses StatusBar on Mobile (header hidden).
    /// </summary>
    public sealed class UnisonThemeStrategy : ShellThemeStrategy
    {
        private static readonly Color BrandGreen = Color.FromArgb(0xFF, 0x20, 0xC0, 0x64);
        private static readonly Color BrandGreenHover = Color.FromArgb(0xFF, 0x1A, 0xA8, 0x54);
        private static readonly Color BrandGreenPressed = Color.FromArgb(0xFF, 0x18, 0x96, 0x4B);
        private static readonly Color BrandWhite = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        private readonly ISystemInfoProvider _systemInfo;

        public UnisonThemeStrategy(ISystemInfoProvider systemInfo)
        {
            _systemInfo = systemInfo;
        }

        public override bool DisplaySyncInChatList =>
            _systemInfo == null || !_systemInfo.IsMobile();

        public override bool UsesMobileStatusBarProgress =>
            _systemInfo != null && _systemInfo.IsMobile();

        public override void SetTitleBar()
        {
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;

                titleBar.BackgroundColor = BrandGreen;
                titleBar.ForegroundColor = BrandWhite;
                titleBar.InactiveBackgroundColor = BrandGreen;
                titleBar.InactiveForegroundColor = BrandWhite;

                titleBar.ButtonBackgroundColor = BrandGreen;
                titleBar.ButtonForegroundColor = BrandWhite;
                titleBar.ButtonHoverBackgroundColor = BrandGreenHover;
                titleBar.ButtonHoverForegroundColor = BrandWhite;
                titleBar.ButtonPressedBackgroundColor = BrandGreenPressed;
                titleBar.ButtonPressedForegroundColor = BrandWhite;
                titleBar.ButtonInactiveBackgroundColor = BrandGreen;
                titleBar.ButtonInactiveForegroundColor = BrandWhite;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UnisonTheme] SetTitleBar: " + ex.Message);
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
                var orientation = DisplayInformation.GetForCurrentView().CurrentOrientation;
                bool portrait =
                    orientation == DisplayOrientations.Portrait ||
                    orientation == DisplayOrientations.PortraitFlipped;

                if (!portrait)
                {
                    try { await statusBar.ProgressIndicator.HideAsync(); } catch { }
                    await statusBar.HideAsync();
                    return;
                }

                statusBar.BackgroundColor = BrandGreen;
                statusBar.ForegroundColor = BrandWhite;
                statusBar.BackgroundOpacity = 1.0;

                await statusBar.ShowAsync();

                // ProgressValue = 0 → label without indeterminate spinner.
                statusBar.ProgressIndicator.Text = "Unison";
                statusBar.ProgressIndicator.ProgressValue = 0;
                await statusBar.ProgressIndicator.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UnisonTheme] SetMobileStatusBar: " + ex.Message);
            }
        }
    }
}
