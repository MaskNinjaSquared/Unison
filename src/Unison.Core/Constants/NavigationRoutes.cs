using System;
using Unison.Core.Models;

namespace Unison.Core.Constants
{
    /// <summary>
    /// String keys for pane <c>Tag</c> / <c>ActiveSection</c> bindings.
    /// Prefer <see cref="NavigationDestination"/> on <see cref="Contracts.INavigator"/>.
    /// </summary>
    public static class NavigationRoutes
    {
        public const string Boot = "boot";
        public const string Start = "start";
        public const string Login = "login";
        public const string AppShell = "appshell";
        public const string Main = "main";

        public const string Chats = "chats";
        public const string Archived = "archived";
        public const string Status = "status";
        public const string Settings = "settings";
        public const string Debug = "debug";

        public static string ToRouteKey(NavigationDestination destination)
        {
            switch (destination)
            {
                case NavigationDestination.Boot: return Boot;
                case NavigationDestination.Start: return Start;
                case NavigationDestination.Login: return Login;
                case NavigationDestination.AppShell: return AppShell;
                case NavigationDestination.Chats: return Chats;
                case NavigationDestination.Archived: return Archived;
                case NavigationDestination.Status: return Status;
                case NavigationDestination.Settings: return Settings;
                case NavigationDestination.Debug: return Debug;
                default: return null;
            }
        }

        public static bool TryParse(string key, out NavigationDestination destination)
        {
            destination = NavigationDestination.Chats;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            if (string.Equals(key, Boot, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Boot;
                return true;
            }

            if (string.Equals(key, Start, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Start;
                return true;
            }

            if (string.Equals(key, Login, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Login;
                return true;
            }

            if (string.Equals(key, AppShell, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, Main, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.AppShell;
                return true;
            }

            if (string.Equals(key, Chats, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Chats;
                return true;
            }

            if (string.Equals(key, Archived, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Archived;
                return true;
            }

            if (string.Equals(key, Status, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Status;
                return true;
            }

            if (string.Equals(key, Settings, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Settings;
                return true;
            }

            if (string.Equals(key, Debug, StringComparison.OrdinalIgnoreCase))
            {
                destination = NavigationDestination.Debug;
                return true;
            }

            return false;
        }
    }
}
