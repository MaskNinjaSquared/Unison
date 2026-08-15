using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Helpers;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.UI.Templates
{
    public sealed partial class ChatItemTemplates : ResourceDictionary
    {
        public ChatItemTemplates()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The title for a row when the chat has no name of its own - a group with no subject
        /// yet, or a contact that is still just a number. Lives here because it is a function
        /// binding in this template and a static is the only shape x:Bind accepts.
        /// </summary>
        public static string GetChatDisplayName(string name, ChatKind kind)
        {
            var item = new ChatItem { Name = name, Kind = kind };
            IStringResources strings = null;
            try
            {
                strings = App.Services?.GetService<IStringResources>();
            }
            catch
            {
            }

            return item.GetNameResolved(strings);
        }

        private void ChatContextFlyout_Opening(object sender, object e)
        {
            var flyout = sender as MenuFlyout;
            var target = flyout?.Target as FrameworkElement;
            var chat = target?.DataContext as ChatItem;
            if (chat == null || flyout == null)
            {
                return;
            }

            try
            {
                App.Services?.GetService<IChatStore>()?.ApplyTo(chat);
            }
            catch
            {
            }

            bool muted = chat.IsMutedLocally;
            foreach (var item in flyout.Items)
            {
                var menuItem = item as MenuFlyoutItem;
                var subItem = item as MenuFlyoutSubItem;
                string tag = (menuItem?.Tag as string) ?? (subItem?.Tag as string);

                if (string.Equals(tag, "chatPin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Text = chat.IsChatPinned
                        ? LocalizedStrings.Get("ChatList_UnpinChat.Text", "Unpin chat")
                        : LocalizedStrings.Get("ChatList_PinChat.Text", "Pin chat");
                    menuItem.Visibility = Visibility.Visible;
                }
                else if (string.Equals(tag, "widgetPin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Text = chat.IsWidgetPinned
                        ? LocalizedStrings.Get("ChatDetail_UnpinFromStart.Text", "Unpin from Start")
                        : LocalizedStrings.Get("ChatDetail_PinToStart.Text", "Pin to Start");
                    menuItem.Visibility = Visibility.Visible;
                }
                else if (string.Equals(tag, "localMuteSub", StringComparison.Ordinal) && subItem != null)
                {
                    // Desmutado → submenu "Silenciar notificações"; mutado → esconde.
                    subItem.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
                    subItem.Text = LocalizedStrings.Get("ChatDetail_MuteNotifications.Text", "Mute notifications");
                    subItem.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
                    foreach (var child in subItem.Items)
                    {
                        var duration = child as MenuFlyoutItem;
                        string durationTag = duration?.Tag as string;
                        if (string.Equals(durationTag, "mute8h", StringComparison.Ordinal))
                        {
                            duration.Text = LocalizedStrings.Get("ChatDetail_MuteFor8Hours.Text", "8 hours");
                        }
                        else if (string.Equals(durationTag, "mute1w", StringComparison.Ordinal))
                        {
                            duration.Text = LocalizedStrings.Get("ChatDetail_MuteFor1Week.Text", "1 week");
                        }
                        else if (string.Equals(durationTag, "muteForever", StringComparison.Ordinal))
                        {
                            duration.Text = LocalizedStrings.Get("ChatDetail_MuteForever.Text", "Always");
                        }
                    }
                }
                else if (string.Equals(tag, "unmute", StringComparison.Ordinal) && menuItem != null)
                {
                    // Mutado → "Ativar notificações"; desmutado → esconde.
                    menuItem.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
                    menuItem.Text = LocalizedStrings.Get("ChatDetail_UnmuteNotifications.Text", "Unmute notifications");
                }
            }
        }

        private void PinChat_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            if (chat != null)
            {
                FindChatList(sender)?.SetChatPinned(chat, !chat.IsChatPinned);
            }
        }

        private void PinToStart_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            FindChatList(sender)?.PinChatToStart(chat);
        }

        private void MuteDuration_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var chat = ResolveChat(element);
            string tag = element?.Tag as string;
            long? until = null;
            if (string.Equals(tag, "mute8h", StringComparison.Ordinal))
            {
                until = ChatMuteHelper.FromNow(ChatMuteHelper.EightHours);
            }
            else if (string.Equals(tag, "mute1w", StringComparison.Ordinal))
            {
                until = ChatMuteHelper.FromNow(ChatMuteHelper.OneWeek);
            }
            else if (string.Equals(tag, "muteForever", StringComparison.Ordinal))
            {
                until = ChatMuteHelper.ForeverUnixSeconds;
            }

            if (until.HasValue)
            {
                FindChatList(sender)?.SetLocalMute(chat, until);
            }
        }

        private void Unmute_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            FindChatList(sender)?.SetLocalMute(chat, null);
        }

        private static ChatItem ResolveChat(FrameworkElement element)
        {
            var chat = element?.DataContext as ChatItem;
            if (chat != null)
            {
                return chat;
            }

            // Walk up from submenu items to the root MenuFlyout target.
            DependencyObject current = element;
            while (current != null)
            {
                var flyout = current as MenuFlyout;
                if (flyout?.Target is FrameworkElement target)
                {
                    return target.DataContext as ChatItem;
                }

                var parent = VisualTreeHelper.GetParent(current);
                if (parent != null)
                {
                    current = parent;
                    continue;
                }

                var flyoutItem = current as MenuFlyoutItem;
                if (flyoutItem?.Parent != null)
                {
                    current = flyoutItem.Parent;
                    continue;
                }

                var sub = current as MenuFlyoutSubItem;
                if (sub?.Parent != null)
                {
                    current = sub.Parent;
                    continue;
                }

                break;
            }

            var asItem = element as MenuFlyoutItem;
            var asFlyout = asItem?.Parent as MenuFlyout;
            return asFlyout?.Target is FrameworkElement t2
                ? t2.DataContext as ChatItem
                : null;
        }

        /// <summary>
        /// ContextFlyout content lives in a popup; walk <see cref="FlyoutBase.Target"/> (Imgur-style), then UI root.
        /// </summary>
        private static ChatListView FindChatList(object sender)
        {
            var flyoutItem = sender as MenuFlyoutItem;
            if (flyoutItem != null)
            {
                DependencyObject current = flyoutItem;
                while (current != null)
                {
                    var flyout = current as MenuFlyout;
                    if (flyout?.Target != null)
                    {
                        var fromTarget = FindAncestor<ChatListView>(flyout.Target);
                        if (fromTarget != null)
                        {
                            return fromTarget;
                        }
                    }

                    var parent = VisualTreeHelper.GetParent(current);
                    if (parent != null)
                    {
                        current = parent;
                        continue;
                    }

                    if (flyoutItem.Parent != null && !ReferenceEquals(current, flyoutItem.Parent))
                    {
                        current = flyoutItem.Parent;
                        continue;
                    }

                    break;
                }
            }

            var fromSender = FindAncestor<ChatListView>(sender as DependencyObject);
            if (fromSender != null)
            {
                return fromSender;
            }

            return FindInSubtree<ChatListView>(Window.Current?.Content as DependencyObject);
        }

        private static T FindAncestor<T>(DependencyObject start) where T : class
        {
            var current = start;
            while (current != null)
            {
                var match = current as T;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static T FindInSubtree<T>(DependencyObject root) where T : class
        {
            if (root == null)
            {
                return null;
            }

            var match = root as T;
            if (match != null)
            {
                return match;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var found = FindInSubtree<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
