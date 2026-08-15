using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Loads Themes/{Unison|WhatsApp}/Theme.xaml, applies shell chrome strategy,
    /// and handles shell change + restart.
    /// </summary>
    public interface IShellThemeService
    {
        /// <summary>
        /// When true, sync text is shown in the chat-list header.
        /// Comes from the active shell strategy (WhatsApp always true; Unison false on Mobile).
        /// </summary>
        bool DisplaySyncInChatList { get; }

        /// <summary>
        /// When true, Mobile StatusBar progress APIs may be used for sync feedback.
        /// </summary>
        bool UsesMobileStatusBarProgress { get; }

        /// <summary>Applies the shell stored in LocalSettings (call early at launch).</summary>
        void ApplyFromSettings();

        /// <summary>Applies title bar + mobile status bar chrome for the active strategy.</summary>
        void ApplyChrome();

        /// <summary>Re-applies mobile status bar only (e.g. orientation change).</summary>
        Task ApplyMobileStatusBarAsync();

        /// <summary>
        /// Persists a new shell, notifies the user, and restarts the app
        /// (Imgur-style settings change that requires relaunch).
        /// No-op if <paramref name="shell"/> is already selected.
        /// </summary>
        Task ChangeShellAndRestartAsync(AppShell shell);
    }
}
