using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Root + shell navigation. Root used for Start / Login / AppShell (no auth backstack).
    /// Shell frame hosts Chats / Status / Debug inside AppShell; Settings via <see cref="OpenSettings"/>.
    /// </summary>
    public interface INavigator
    {
        void Navigate(NavigationDestination destination, object parameter = null);

        /// <summary>Navigate root and clear back stack (Start ↔ Login ↔ Shell).</summary>
        void NavigateAndClear(NavigationDestination destination, object parameter = null);

        void GoBack();
        bool CanGoBack { get; }
        void ClearBackStack();

        /// <summary>Wire the SplitView content Frame (called by AppShell / MainView).</summary>
        void AttachShellFrame(object frame);

        void NavigateInShell(NavigationDestination destination, object parameter = null);

        /// <summary>Navigate shell content and drop shell back stack (section switches).</summary>
        void NavigateInShellAndClear(NavigationDestination destination, object parameter = null);

        /// <summary>
        /// Open settings for the active shell strategy (Unison: shell page; WhatsApp may use a
        /// dialog later — today still a shell page under <c>Shell/WhatsApp</c>).
        /// </summary>
        /// <param name="openProfile">
        /// When true, Unison opens settings already in the expanded profile,
        /// and WhatsApp selects the profile section.
        /// </param>
        void OpenSettings(bool openProfile = false);

        void GoBackInShell();
        bool CanGoBackInShell { get; }

        /// <summary>Current shell content route key (<see cref="Constants.NavigationRoutes"/>), or null.</summary>
        string CurrentShellRoute { get; }

        /// <summary>Raised after shell frame navigation (forward or back). Arg is <see cref="CurrentShellRoute"/>.</summary>
        event System.EventHandler<string> ShellNavigated;

        /// <summary>
        /// Wire the full-screen Frame that covers the whole shell (sidebar / pivot / detail).
        /// Unison and WhatsApp: overlay Frame on MainView. Legacy: its Settings/Debug overlay Frame.
        /// </summary>
        void AttachFullScreenFrame(object frame);

        /// <summary>
        /// Open a full-screen page over the shell. The active shell strategy decides which
        /// destinations are full-screen (Legacy: Settings / Debug; every shell: StatusComposer).
        /// </summary>
        void NavigateFullScreen(NavigationDestination destination, object parameter = null);

        /// <summary>Close the full-screen page and drop its stack; no-op when nothing is open.</summary>
        void CloseFullScreen();

        bool IsFullScreenOpen { get; }

        /// <summary>Raised after a full-screen page opens or closes.</summary>
        event System.EventHandler FullScreenChanged;

        /// <summary>
        /// Drop shell back/forward stacks and disable cache on the current shell page
        /// so logout/login does not revive a stale Chats/Settings instance.
        /// </summary>
        void PurgeShellNavigation();
    }
}
