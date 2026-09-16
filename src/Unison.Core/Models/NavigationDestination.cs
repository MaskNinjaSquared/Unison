namespace Unison.Core.Models
{
    /// <summary>
    /// Semantic navigation targets for <see cref="Contracts.INavigator"/>.
    /// Page types live only in the UWP shell strategy map — Core never names a view.
    /// Settings is opened via <see cref="Contracts.INavigator.OpenSettings"/> (surface may be a
    /// shell page or a dialog depending on the active <see cref="AppShell"/>).
    /// </summary>
    public enum NavigationDestination
    {
        Boot = 0,
        Start = 1,
        Login = 2,
        AppShell = 3,

        Chats = 10,
        Status = 11,
        Debug = 12,
        Archived = 13,

        /// <summary>
        /// Tracked as the active section when settings is open; do not pass to
        /// <c>NavigateInShell</c> — use <see cref="Contracts.INavigator.OpenSettings"/>.
        /// </summary>
        Settings = 20
    }
}
