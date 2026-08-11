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

        /// <summary>Wire the SplitView content Frame (called by AppShell / MainPage).</summary>
        void AttachShellFrame(object frame);

        void NavigateInShell(string destination, object parameter = null);

        /// <summary>Navigate shell content and drop shell back stack (section switches).</summary>
        void NavigateInShellAndClear(string destination, object parameter = null);

        void GoBackInShell();
        bool CanGoBackInShell { get; }
    }
}
