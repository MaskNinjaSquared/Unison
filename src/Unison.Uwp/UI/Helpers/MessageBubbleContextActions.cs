using System.Diagnostics;
using Unison.Core.ViewModels;
using Unison.Uwp.Helpers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Copy, Reply, and Forward on every bubble menu.
    /// Forward opens the shell people picker (person click is a stub).
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
        }

        private static MenuFlyoutItem Item(
            string label,
            string glyph,
            System.Windows.Input.ICommand command,
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
