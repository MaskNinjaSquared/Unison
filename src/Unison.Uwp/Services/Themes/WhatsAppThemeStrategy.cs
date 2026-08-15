using Windows.UI;
using Windows.UI.ViewManagement;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// WhatsApp shell: OS-default title bar background; green caption text + min/max/close glyphs.
    /// Sync feedback always stays in the chat-list header.
    /// </summary>
    public sealed class WhatsAppThemeStrategy : ShellThemeStrategy
    {
        /// <summary>Matches WhatsAppGreenFocusBrush (#1DAA61).</summary>
        private static readonly Color CaptionGreen = Color.FromArgb(0xFF, 0x1D, 0xAA, 0x61);

        /// <summary>Slightly brighter glyph on hover (#25D366).</summary>
        private static readonly Color CaptionGreenHover = Color.FromArgb(0xFF, 0x25, 0xD3, 0x66);

        public override bool DisplaySyncInChatList => true;

        public override bool UsesMobileStatusBarProgress => false;

        /// <summary>
        /// Keep the system caption background; only tint title + window buttons green.
        /// Explicit null / Transparent clears any leftover Unison green fill from an in-process shell switch.
        /// </summary>
        public override void SetTitleBar()
        {
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;

                // Bar fill = OS default (no green strip).
                titleBar.BackgroundColor = null;
                titleBar.InactiveBackgroundColor = null;

                titleBar.ForegroundColor = CaptionGreen;
                titleBar.InactiveForegroundColor = CaptionGreen;

                // Glyphs only — transparent button chrome over the default bar.
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
                titleBar.ButtonPressedBackgroundColor = Colors.Transparent;

                titleBar.ButtonForegroundColor = CaptionGreen;
                titleBar.ButtonInactiveForegroundColor = CaptionGreen;
                titleBar.ButtonHoverForegroundColor = CaptionGreenHover;
                titleBar.ButtonPressedForegroundColor = CaptionGreen;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppTheme] SetTitleBar: " + ex.Message);
            }
        }
    }
}
