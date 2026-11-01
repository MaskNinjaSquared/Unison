// =============================================================================
// MessageReactionContextChrome
//
// One Flyout: quick-reaction bar (+ optional action rows). Never opens a second
// MenuFlyout on the same anchor — UWP dismisses the first surface.
// Plus dismisses the Flyout and asks the host to raise the bottom emoji dock
// (same pattern as the attachment bar).
// =============================================================================
using System;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Unison.Uwp.UI.Helpers
{
    internal static class MessageReactionContextChrome
    {
        private const double ScreenMargin = 24;

        /// <summary>Shared width for quick bar + action rows (clamped to the screen).</summary>
        private const double ChromeWidth = 250;

        /// <summary>
        /// Opens the reaction quick bar (and optional <paramref name="actionsHost"/>) in one Flyout.
        /// <paramref name="openFullPicker"/> raises the host bottom dock (attachment-style).
        /// </summary>
        public static Flyout Show(
            FrameworkElement anchor,
            ChatMessage message,
            ChatDetailViewModel detailVm,
            FrameworkElement actionsHost = null,
            Action<ChatMessage> openFullPicker = null)
        {
            if (anchor == null || message == null)
            {
                return null;
            }

            double width = ResolveChromeWidth();
            string selectedEmoji = detailVm?.GetMyReactionEmoji(message);
            var quickBar = new ReactionQuickBar
            {
                Width = width,
                MaxWidth = width,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedEmoji = selectedEmoji
            };

            var quickFlyout = new Flyout
            {
                Placement = FlyoutPlacementMode.Top,
                FlyoutPresenterStyle = CreateTransparentPresenterStyle(width)
            };

            UIElement content = quickBar;
            if (actionsHost != null)
            {
                quickBar.HorizontalAlignment = HorizontalAlignment.Stretch;
                actionsHost.Width = width;
                actionsHost.MaxWidth = width;
                actionsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                var root = new StackPanel
                {
                    Width = width,
                    MaxWidth = width
                };
                root.Children.Add(quickBar);
                actionsHost.Margin = new Thickness(0, 6, 0, 0);
                root.Children.Add(actionsHost);
                content = root;
            }

            quickFlyout.Content = content;

            quickBar.EmojiChosen += async (s, emoji) =>
            {
                quickFlyout.Hide();
                if (detailVm != null)
                {
                    await detailVm.ReactToMessageAsync(message, emoji).ConfigureAwait(true);
                }
            };

            quickBar.PlusClicked += (s, e) =>
            {
                quickFlyout.Hide();
                openFullPicker?.Invoke(message);
            };

            try
            {
                quickFlyout.ShowAt(anchor);
            }
            catch
            {
                // Placement may fail on tiny anchors.
            }

            return quickFlyout;
        }

        private static double ResolveChromeWidth()
        {
            try
            {
                double screen = Window.Current?.Bounds.Width ?? 0;
                if (screen > ScreenMargin * 2)
                {
                    return Math.Min(ChromeWidth, screen - ScreenMargin);
                }
            }
            catch
            {
            }

            return ChromeWidth;
        }

        private static Style CreateTransparentPresenterStyle(double maxWidth)
        {
            var style = new Style(typeof(FlyoutPresenter));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            // UWP rejects Setter(Background, null) with ArgumentException("value").
            style.Setters.Add(new Setter(
                Control.BackgroundProperty,
                new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent)));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, maxWidth));
            return style;
        }
    }
}
