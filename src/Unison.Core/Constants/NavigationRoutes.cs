namespace Unison.Core.Constants
{
    /// <summary>Route keys for <see cref="Contracts.INavigator"/> (root + shell content).</summary>
    public static class NavigationRoutes
    {
        // Root frame (full pages; back stack cleared across auth boundaries)
        public const string Boot = "boot";
        public const string Start = "start";
        public const string Login = "login";
        public const string AppShell = "appshell";
        public const string Main = "main"; // alias → AppShell

        // Shell content frame (inside AppShell SplitView)
        public const string Chats = "chats";
        public const string Settings = "settings";
        public const string Debug = "debug";
    }
}
