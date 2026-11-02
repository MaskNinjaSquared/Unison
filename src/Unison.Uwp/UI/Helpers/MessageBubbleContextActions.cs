using System;
using System.Diagnostics;
using System.Windows.Input;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Copy, Reply, Forward (and optional Delete) on bubble context chrome.
    /// Forward opens the shell people picker (person click is a stub).
    /// </summary>
    internal static class MessageBubbleContextActions
    {
        /// <summary>Opaque WP/W10M chrome (matches ModernMenuFlyoutStyle).</summary>
        private static readonly Color HostBackground = Color.FromArgb(0xFF, 0x23, 0x23, 0x23);

        private static readonly Color HostBorder = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A);

        private static readonly Color Separator = Color.FromArgb(0xFF, 0x3A, 0x3A, 0x3A);

        private static readonly Color RowForeground = Colors.White;

        public static void Add(MenuFlyout flyout, ChatMessageViewModel vm)
        {
            if (flyout == null || vm == null)
            {
                return;
            }

            flyout.Items.Add(Item(
                LocalizedStrings.Get("ChatDetail_CopyMessage", "Copy"),
                "\uE8C8",
                vm.CopyCommand));
            flyout.Items.Add(Item(
                LocalizedStrings.Get("ChatDetail_ReplyMessage", "Reply"),
                "\uE97A",
                vm.ReplyCommand));
            // Same forward glyph as the bubble "Forwarded" label (EA54); MDL2, not Fluent IconFont.
            bool canForward = vm.ForwardCommand != null && vm.ForwardCommand.CanExecute(null);
            Debug.WriteLine(
                "[Forward] MenuFlyout add Forward; id=" + (vm.Model?.Id ?? "(null)") +
                "; CanExecute=" + canForward);
            flyout.Items.Add(Item(
                LocalizedStrings.Get("ChatDetail_ForwardMessage", "Forward"),
                "\uEA54",
                vm.ForwardCommand,
                useMdl2: true));

            if (vm.DeleteCommand != null && vm.DeleteCommand.CanExecute(null))
            {
                flyout.Items.Add(Item(
                    LocalizedStrings.Get("ChatDetail_Delete", "Delete"),
                    "\uE74D",
                    vm.DeleteCommand));
            }
        }

        /// <summary>
        /// Same actions as <see cref="Add"/>, as buttons for the single reaction Flyout
        /// (UWP dismisses a Flyout when a second MenuFlyout opens on the same anchor).
        /// </summary>
        public static void AddButtons(Panel panel, ChatMessageViewModel vm, Action invoked = null)
        {
            if (panel == null || vm == null)
            {
                return;
            }

            panel.Children.Add(CommandButton(
                LocalizedStrings.Get("ChatDetail_CopyMessage", "Copy"),
                "\uE8C8",
                vm.CopyCommand,
                invoked));
            panel.Children.Add(CommandButton(
                LocalizedStrings.Get("ChatDetail_ReplyMessage", "Reply"),
                "\uE97A",
                vm.ReplyCommand,
                invoked));
            bool canForward = vm.ForwardCommand != null && vm.ForwardCommand.CanExecute(null);
            Debug.WriteLine(
                "[Forward] Flyout add Forward; id=" + (vm.Model?.Id ?? "(null)") +
                "; CanExecute=" + canForward);
            panel.Children.Add(CommandButton(
                LocalizedStrings.Get("ChatDetail_ForwardMessage", "Forward"),
                "\uEA54",
                vm.ForwardCommand,
                invoked,
                useMdl2: true));

            if (vm.DeleteCommand != null && vm.DeleteCommand.CanExecute(null))
            {
                panel.Children.Add(CommandButton(
                    LocalizedStrings.Get("ChatDetail_Delete", "Delete"),
                    "\uE74D",
                    vm.DeleteCommand,
                    invoked));
            }
        }

        /// <summary>Hairline between primary actions and pin group (WP context menu).</summary>
        public static void AddSeparator(Panel panel)
        {
            if (panel == null)
            {
                return;
            }

            panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 4),
                Background = new SolidColorBrush(Separator),
                HorizontalAlignment = HorizontalAlignment.Stretch
            });
        }

        /// <summary>
        /// Solid, low-radius host — closer to W10 Mobile / WP8 context menus than Fluent cards.
        /// </summary>
        public static Border WrapActionsHost(UIElement content)
        {
            return new Border
            {
                Child = content,
                Background = new SolidColorBrush(HostBackground),
                BorderBrush = new SolidColorBrush(HostBorder),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(0, 4, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        public static Button ActionButton(
            string label,
            string glyph,
            Action click,
            bool useMdl2 = false)
        {
            var button = BuildChromeButton(label, glyph, useMdl2);
            button.Click += (s, e) => click?.Invoke();
            return button;
        }

        private static Button CommandButton(
            string label,
            string glyph,
            ICommand command,
            Action invoked,
            bool useMdl2 = false)
        {
            var button = BuildChromeButton(label, glyph, useMdl2);
            button.Command = command;
            button.Click += (s, e) => invoked?.Invoke();
            return button;
        }

        private static Button BuildChromeButton(string label, string glyph, bool useMdl2)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            FontFamily iconFont = ResolveIconFont(useMdl2);
            if (iconFont != null && !string.IsNullOrEmpty(glyph))
            {
                var icon = new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = iconFont,
                    FontSize = 16,
                    Foreground = new SolidColorBrush(RowForeground),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(icon, 0);
                row.Children.Add(icon);
            }

            var text = new TextBlock
            {
                Text = label ?? string.Empty,
                FontSize = 15,
                Foreground = new SolidColorBrush(RowForeground),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            };
            try
            {
                text.FontFamily = (FontFamily)Application.Current.Resources["MainFont"];
            }
            catch
            {
            }

            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            return new Button
            {
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 44,
                Padding = new Thickness(8, 0, 16, 0),
                Margin = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                BorderBrush = new SolidColorBrush(Colors.Transparent)
            };
        }

        private static MenuFlyoutItem Item(
            string label,
            string glyph,
            ICommand command,
            bool useMdl2 = false)
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Command = command
            };
            FontFamily iconFont = ResolveIconFont(useMdl2);
            if (iconFont != null)
            {
                item.Icon = new FontIcon { Glyph = glyph, FontFamily = iconFont };
            }

            return item;
        }

        private static FontFamily ResolveIconFont(bool useMdl2)
        {
            if (useMdl2)
            {
                return new FontFamily("Segoe MDL2 Assets");
            }

            try
            {
                return (FontFamily)Application.Current.Resources["IconFont"];
            }
            catch
            {
                return null;
            }
        }
    }
}
