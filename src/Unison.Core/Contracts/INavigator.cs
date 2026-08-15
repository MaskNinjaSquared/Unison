namespace Unison.Core.Contracts
{
    /// <summary>
    /// Root + shell navigation. Root used for Start / Login / AppShell (no auth backstack).
    /// Shell frame hosts Chats / Settings / Debug inside AppShell.
    /// </summary>
    public interface INavigator
    {
        void Navigate(string destination, object parameter = null);

        /// <summary>Navigate root and clear back stack (Start ↔ Login ↔ Shell).</summary>
        void NavigateAndClear(string destination, object parameter = null);

        void GoBack();
        bool CanGoBack { get; }
        void ClearBackStack();

        /// <summary>Wire the SplitView content Frame (called by AppShell / MainView).</summary>
        void AttachShellFrame(object frame);

        void NavigateInShell(string destination, object parameter = null);

        /// <summary>Navigate shell content and drop shell back stack (section switches).</summary>
        void NavigateInShellAndClear(string destination, object parameter = null);

        void GoBackInShell();
        bool CanGoBackInShell { get; }

        /// <summary>Current shell content route (<see cref="Constants.NavigationRoutes"/>), or null.</summary>
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
