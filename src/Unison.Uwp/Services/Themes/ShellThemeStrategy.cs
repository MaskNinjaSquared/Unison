using System;
using Unison.Core.Models;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// Per-shell chrome + navigation map. Defaults keep shared <c>UI/Views</c> pages;
    /// Settings is opened via <see cref="OpenSettings"/> so WhatsApp can later use a dialog.
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
        public virtual System.Threading.Tasks.Task SetMobileStatusBarAsync()
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>Root frame page for Boot / Start / Login / AppShell.</summary>
        public virtual Type ResolveRootPage(NavigationDestination destination)
        {
            switch (destination)
            {
                case NavigationDestination.Boot:
                    return typeof(UI.Views.BootView);
                case NavigationDestination.Start:
                    return typeof(UI.Views.StartView);
                case NavigationDestination.Login:
                    return typeof(UI.Views.LoginView);
                case NavigationDestination.AppShell:
                    return typeof(MainView);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(destination),
                        destination,
                        "Not a root navigation destination.");
            }
        }

        /// <summary>
        /// Shell content page for Chats / Status / Debug.
        /// Settings is not mapped here — use <see cref="OpenSettings"/>.
        /// </summary>
        public virtual Type ResolveShellPage(NavigationDestination destination)
        {
            switch (destination)
            {
                case NavigationDestination.Chats:
                    return typeof(UI.Views.ChatsView);
                case NavigationDestination.Archived:
                    return typeof(UI.Views.ArchivedChatsView);
                case NavigationDestination.Status:
                    return typeof(UI.Views.StatusView);
                case NavigationDestination.Debug:
                    return typeof(UI.Views.DebugView);
                case NavigationDestination.Settings:
                    throw new InvalidOperationException(
                        "Settings is opened via OpenSettings(), not ResolveShellPage.");
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(destination),
                        destination,
                        "Not a shell content destination.");
            }
        }

        /// <summary>
        /// Opens settings for this shell. Unison/WhatsApp both navigate to their
        /// <c>Shell/*/Views/SettingsView</c> today; WhatsApp may switch to a dialog later.
        /// </summary>
        public abstract void OpenSettings(Frame shellFrame);

        /// <summary>True when <paramref name="content"/> is this shell's settings surface.</summary>
        public abstract bool IsSettingsPage(object content);

        /// <summary>Maps a live shell Frame.Content instance back to a destination.</summary>
        public virtual bool TryResolveShellDestination(object content, out NavigationDestination destination)
        {
            destination = NavigationDestination.Chats;
            if (content == null)
            {
                return false;
            }

            if (IsSettingsPage(content))
            {
                destination = NavigationDestination.Settings;
                return true;
            }

            if (content is UI.Views.DebugView)
            {
                destination = NavigationDestination.Debug;
                return true;
            }

            if (content is UI.Views.StatusView)
            {
                destination = NavigationDestination.Status;
                return true;
            }

            if (content is UI.Views.ArchivedChatsView)
            {
                destination = NavigationDestination.Archived;
                return true;
            }

            if (content is UI.Views.ChatsView)
            {
                destination = NavigationDestination.Chats;
                return true;
            }

            return false;
        }

        /// <summary>Shared Frame.Navigate helper for shell strategies.</summary>
        protected static void NavigateShellFrame(Frame shellFrame, Type pageType, object parameter = null)
        {
            if (shellFrame == null)
            {
                throw new InvalidOperationException("Shell frame is not ready for settings.");
            }

            if (pageType == null)
            {
                throw new ArgumentNullException(nameof(pageType));
            }

            if (shellFrame.Content != null && shellFrame.Content.GetType() == pageType)
            {
                return;
            }

            shellFrame.Navigate(pageType, parameter);
        }
    }
}
