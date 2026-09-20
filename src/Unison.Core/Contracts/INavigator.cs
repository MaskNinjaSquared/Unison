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
        void OpenSettings();

        void GoBackInShell();
        bool CanGoBackInShell { get; }

        /// <summary>Current shell content route key (<see cref="Constants.NavigationRoutes"/>), or null.</summary>
        string CurrentShellRoute { get; }

        /// <summary>Raised after shell frame navigation (forward or back). Arg is <see cref="CurrentShellRoute"/>.</summary>
        event System.EventHandler<string> ShellNavigated;

        /// <summary>
        /// Drop shell back/forward stacks and disable cache on the current shell page
        /// so logout/login does not revive a stale Chats/Settings instance.
        /// </summary>
        void PurgeShellNavigation();
    }
}
