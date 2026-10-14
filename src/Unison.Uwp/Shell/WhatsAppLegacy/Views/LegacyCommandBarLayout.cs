using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>
    /// Centers the primary app-bar buttons in the strip beside the overflow (…) button.
    /// The stock template keeps that column only as wide as the buttons and pins them
    /// to the right, so they sit against the ellipsis instead of the middle.
    /// </summary>
    internal static class LegacyCommandBarLayout
    {
        // AppBarExpandButtonThemeWidth. Mirrored on the left so the strip is centered on the
        // whole bar. Applied inside ContentRoot: CommandBar.Padding would leave it unpainted,
        // since the template's LayoutRoot has no background.
        private const double MoreButtonWidth = 48;

        public static void CenterPrimaryCommands(CommandBar bar)
        {
            if (bar == null)
            {
                return;
            }

            Apply(bar);
            // Display-mode states can reset the column after Loaded. Re-apply once they settle.
            var ignored = bar.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => Apply(bar));
        }

        private static void Apply(CommandBar bar)
        {
            var primary = FindDescendant(bar, "PrimaryItemsControl") as FrameworkElement;
            if (primary == null)
            {
                return;
            }

            // Fill the strip beside the more button, then center the icon row inside it.
            // Centering the items control itself does nothing once that strip stretches it.
            primary.HorizontalAlignment = HorizontalAlignment.Stretch;
            primary.VerticalAlignment = VerticalAlignment.Top;
            StackPanel icons = FindStackPanel(primary);
            if (icons != null)
            {
                icons.HorizontalAlignment = HorizontalAlignment.Center;
                icons.VerticalAlignment = VerticalAlignment.Center;
            }

            var host = VisualTreeHelper.GetParent(primary) as Grid;
            if (host == null || host.ColumnDefinitions.Count < 2)
            {
                return;
            }

            // Column 0 is the empty content slot. Column 1 holds the buttons.
            // The overflow button is the next column out, so this grid is already
            // the width left of it: give that width to the buttons and center them.
            GridLength content = host.ColumnDefinitions[0].Width;
            if (content.GridUnitType != GridUnitType.Pixel || content.Value != 0)
            {
                host.ColumnDefinitions[0].Width = new GridLength(0);
            }

            if (host.ColumnDefinitions[1].Width.GridUnitType != GridUnitType.Star)
            {
                host.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
            }

            var more = FindDescendant(bar, "MoreButton") as UIElement;
            bool hasMore = more != null && more.Visibility == Visibility.Visible;
            host.Margin = new Thickness(hasMore ? MoreButtonWidth : 0, 0, 0, 0);
        }

        private static StackPanel FindStackPanel(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var panel = child as StackPanel;
                if (panel != null)
                {
                    return panel;
                }

                StackPanel nested = FindStackPanel(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static DependencyObject FindDescendant(DependencyObject root, string name)
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var element = child as FrameworkElement;
                if (element != null && string.Equals(element.Name, name, System.StringComparison.Ordinal))
                {
                    return child;
                }

                DependencyObject nested = FindDescendant(child, name);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
