using Windows.UI.ViewManagement;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// WhatsApp shell: system title/status bar (no Unison green chrome).
    /// Sync feedback always stays in the chat-list header.
    /// </summary>
    public sealed class WhatsAppThemeStrategy : ShellThemeStrategy
    {
        public override bool DisplaySyncInChatList => true;

        public override bool UsesMobileStatusBarProgress => false;

        /// <summary>
        /// Restore OS defaults. A no-op would leave Unison green if it was set earlier
        /// in-process, or leave Accent-tinted caption chrome on some builds.
        /// </summary>
        public override void SetTitleBar()
        {
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;

                // null = Windows default (original UWP caption chrome).
                titleBar.BackgroundColor = null;
                titleBar.ForegroundColor = null;
                titleBar.InactiveBackgroundColor = null;
                titleBar.InactiveForegroundColor = null;

                titleBar.ButtonBackgroundColor = null;
                titleBar.ButtonForegroundColor = null;
                titleBar.ButtonHoverBackgroundColor = null;
                titleBar.ButtonHoverForegroundColor = null;
                titleBar.ButtonPressedBackgroundColor = null;
                titleBar.ButtonPressedForegroundColor = null;
                titleBar.ButtonInactiveBackgroundColor = null;
                titleBar.ButtonInactiveForegroundColor = null;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppTheme] SetTitleBar: " + ex.Message);
            }
        }
    }
}
