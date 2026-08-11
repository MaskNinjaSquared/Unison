using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// StatusBar progress for Unison Mobile sync feedback only.
    /// WhatsApp shell (and desktop) → no-op via <see cref="IShellThemeService.UsesMobileStatusBarProgress"/>.
    /// </summary>
    public sealed class StatusBarService : IStatusBarService
    {
        private static readonly Color BrandGreen = Color.FromArgb(0xFF, 0x20, 0xC0, 0x64);
        private static readonly Color BrandWhite = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        private readonly IShellThemeService _theme;

        public StatusBarService(IShellThemeService theme)
        {
            _theme = theme;
        }

        public bool IsAvailable =>
            ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar");

        public async Task ShowProgressAsync(string text)
        {
            if (!IsAvailable || _theme == null || !_theme.UsesMobileStatusBarProgress)
            {
                return;
            }

            try
            {
                // Landscape: ignore — do not re-show chrome/progress over the full-bleed UI.
                if (!IsPortrait())
                {
                    return;
                }

                var statusBar = StatusBar.GetForCurrentView();
                statusBar.BackgroundColor = BrandGreen;
                statusBar.ForegroundColor = BrandWhite;
                statusBar.BackgroundOpacity = 1.0;
                await statusBar.ShowAsync();

                // null ProgressValue → indeterminate "..." indicator + label.
                statusBar.ProgressIndicator.Text = text ?? string.Empty;
                statusBar.ProgressIndicator.ProgressValue = null;
                await statusBar.ProgressIndicator.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[StatusBarService] ShowProgress: " + ex.Message);
            }
        }

        public async Task HideProgressAsync()
        {
            if (!IsAvailable || _theme == null || !_theme.UsesMobileStatusBarProgress)
            {
                return;
            }

            try
            {
                var statusBar = StatusBar.GetForCurrentView();
                try { await statusBar.ProgressIndicator.HideAsync(); } catch { }

                if (!IsPortrait())
                {
                    await statusBar.HideAsync();
                    return;
                }

                // Restore brand label without spinner (same as Unison chrome).
                statusBar.BackgroundColor = BrandGreen;
                statusBar.ForegroundColor = BrandWhite;
                statusBar.BackgroundOpacity = 1.0;
                await statusBar.ShowAsync();
                statusBar.ProgressIndicator.Text = "Unison";
                statusBar.ProgressIndicator.ProgressValue = 0;
                await statusBar.ProgressIndicator.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[StatusBarService] HideProgress: " + ex.Message);
            }
        }

        private static bool IsPortrait()
        {
            try
            {
                var orientation = DisplayInformation.GetForCurrentView().CurrentOrientation;
                return orientation == DisplayOrientations.Portrait ||
                       orientation == DisplayOrientations.PortraitFlipped;
            }
            catch
            {
                return true;
            }
        }
    }
}
