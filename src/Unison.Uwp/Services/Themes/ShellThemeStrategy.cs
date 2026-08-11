using System.Threading.Tasks;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// Per-shell chrome hooks. Defaults are no-ops so WhatsApp can stay 1:1 with Android.
    /// </summary>
    public abstract class ShellThemeStrategy
    {
        /// <summary>
        /// When true, sync feedback is shown in the chat-list header.
        /// When false, Mobile status-bar progress is used instead (Unison Mobile).
        /// </summary>
        public virtual bool DisplaySyncInChatList => true;

        /// <summary>
        /// When true, <see cref="StatusBarService"/> may show/hide sync progress on Mobile.
        /// </summary>
        public virtual bool UsesMobileStatusBarProgress => false;

        /// <summary>PC caption / title-bar colors.</summary>
        public virtual void SetTitleBar()
        {
        }

        /// <summary>Mobile StatusBar chrome (portrait brand bar / landscape hide).</summary>
        public virtual Task SetMobileStatusBarAsync()
        {
            return Task.CompletedTask;
        }
    }
}
