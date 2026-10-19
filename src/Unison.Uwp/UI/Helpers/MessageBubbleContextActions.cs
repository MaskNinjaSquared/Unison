using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Copy and Reply on every bubble menu. Reply is wired and does nothing yet.
    /// </summary>
    internal static class MessageBubbleContextActions
    {
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
        }

        private static MenuFlyoutItem Item(string label, string glyph, System.Windows.Input.ICommand command)
        {
            var item = new MenuFlyoutItem
            {
                Text = label,
                Command = command
            };
            FontFamily iconFont = null;
            try
            {
                iconFont = (FontFamily)Application.Current.Resources["IconFont"];
            }
            catch
            {
            }

            if (iconFont != null)
            {
                item.Icon = new FontIcon { Glyph = glyph, FontFamily = iconFont };
            }

            return item;
        }
    }
}
