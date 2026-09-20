using System;
using Unison.Core.Models;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// UWP-only navigation map for the active <see cref="AppShell"/> strategy.
    /// Implemented by <see cref="ShellThemeService"/>.
    /// </summary>
    public interface IShellNavigationStrategy
    {
        Type ResolveRootPage(NavigationDestination destination);

        Type ResolveShellPage(NavigationDestination destination);

        void OpenSettings(Frame shellFrame);

        bool IsSettingsPage(object content);

        bool TryResolveShellDestination(object content, out NavigationDestination destination);
    }
}
