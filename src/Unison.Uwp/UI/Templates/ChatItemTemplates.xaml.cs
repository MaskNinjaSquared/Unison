using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Services;
using Unison.Uwp.UI.Views;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.UI.Templates
{
    public sealed partial class ChatItemTemplates : ResourceDictionary
    {
        private const string RowMenuName = "ChatRowMenu";

        /// <summary>
        /// Legacy Mobile Hold must not let the platform auto-open Grid.ContextFlyout.
        /// Stash is keyed by the stable <see cref="ListViewItem"/> (not the template Grid):
        /// recycling can replace the Grid and drop a Grid-keyed ConditionalWeakTable entry,
        /// which left ResolveRowContextMenu null on right-click.
        /// </summary>
        private static readonly ConditionalWeakTable<FrameworkElement, MenuFlyout> DetachedRowMenus =
            new ConditionalWeakTable<FrameworkElement, MenuFlyout>();

        public ChatItemTemplates()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows the row menu at <paramref name="position"/> in the <paramref name="anchor"/>'s
        /// coordinates. Always opens against the template <c>Grid</c> (feature/2.1), so
        /// <see cref="FlyoutBase.Target"/>.DataContext is the <see cref="ChatItem"/>.
        /// </summary>
        public static bool TryShowRowContextMenu(FrameworkElement anchor, Point position = default(Point))
        {
            if (anchor == null)
            {
                Debug.WriteLine("[ChatListContext] Templates.TryShow: anchor null");
                return false;
            }

            // Recycle may have left ContextFlyout cleared without a ListViewItem stash — retry.
            if (ResolveRowContextMenu(anchor) == null)
            {
                DetachRowContextFlyoutForLegacyHold(anchor);
            }

            if (ResolveRowContextMenu(anchor) == null)
            {
                EnsureRowContextMenuFromTemplate(anchor);
            }

            FrameworkElement root = ResolveMenuPlacementRoot(anchor);
            MenuFlyout flyout = ResolveRowContextMenu(anchor);
            if (root == null || flyout == null)
            {
                Debug.WriteLine(
                    "[ChatListContext] Templates.TryShow: root=" + (root != null) +
                    "; flyout=" + (flyout != null) +
                    "; anchor=" + anchor.GetType().Name +
                    "; container=" + (FindListViewItem(anchor)?.GetType().Name ?? "(null)"));
                return false;
            }

            // ShowAt(root): Target is the row Grid → DataContext is ChatItem (feature/2.1).

            Point atRoot = position;
            if (!double.IsNaN(position.X) &&
                !double.IsNaN(position.Y) &&
                !ReferenceEquals(anchor, root))
            {
                try
                {
                    atRoot = anchor.TransformToVisual(root).TransformPoint(position);
                }
                catch
                {
                    atRoot = new Point(double.NaN, double.NaN);
                }
            }

            try
            {
                if (double.IsNaN(atRoot.X) || double.IsNaN(atRoot.Y))
                {
                    flyout.ShowAt(root);
                }
                else
                {
                    flyout.ShowAt(root, atRoot);
                }

                Debug.WriteLine(
                    "[ChatListContext] Templates.ShowAt OK; root=" + root.GetType().Name);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListContext] Templates.ShowAt failed: " + ex.Message);
                try
                {
                    flyout.ShowAt(root);
                    Debug.WriteLine("[ChatListContext] Templates.ShowAt fallback OK");
                    return true;
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine(
                        "[ChatListContext] Templates.ShowAt fallback failed: " + ex2.Message);
                    return false;
                }
            }
        }

        /// <summary>Legacy/callers that do not need a shown/failed result.</summary>
        public static void ShowRowContextMenu(FrameworkElement anchor, Point position = default(Point))
        {
            TryShowRowContextMenu(anchor, position);
        }

        public static MenuFlyout ResolveRowContextMenu(FrameworkElement anchor)
        {
            ListViewItem container = FindListViewItem(anchor);
            MenuFlyout fromContainer = TryGetDetached(container);
            if (fromContainer != null)
            {
                return fromContainer;
            }

            FrameworkElement root = ResolveMenuPlacementRoot(anchor);
            MenuFlyout fromRoot = TryMenuFromElement(root);
            if (fromRoot != null)
            {
                // Promote Grid stash → container so recycle keeps the flyout.
                StashDetached(container, fromRoot);
                return fromRoot;
            }

            return FindMenuInSubtree(anchor);
        }

        private static FrameworkElement ResolveMenuPlacementRoot(FrameworkElement anchor)
        {
            FrameworkElement root = ResolveTemplateRoot(anchor);
            if (TryMenuFromElement(root) != null)
            {
                return root;
            }

            FrameworkElement fromTree = FindMenuOwnerInSubtree(anchor);
            return fromTree ?? root;
        }

        private static MenuFlyout TryMenuFromElement(FrameworkElement element)
        {
            if (element == null)
            {
                return null;
            }

            MenuFlyout detached = TryGetDetached(element);
            if (detached != null)
            {
                return detached;
            }

            return element.ContextFlyout as MenuFlyout
                ?? element.FindName(RowMenuName) as MenuFlyout;
        }

        private static MenuFlyout TryGetDetached(FrameworkElement element)
        {
            if (element == null)
            {
                return null;
            }

            MenuFlyout detached;
            return DetachedRowMenus.TryGetValue(element, out detached) && detached != null
                ? detached
                : null;
        }

        private static void StashDetached(FrameworkElement key, MenuFlyout flyout)
        {
            if (key == null || flyout == null || TryGetDetached(key) != null)
            {
                return;
            }

            try
            {
                DetachedRowMenus.Add(key, flyout);
            }
            catch (ArgumentException)
            {
            }
        }

        private static MenuFlyout FindMenuInSubtree(DependencyObject start)
        {
            FrameworkElement owner = FindMenuOwnerInSubtree(start);
            return owner == null ? null : TryMenuFromElement(owner);
        }

        private static FrameworkElement FindMenuOwnerInSubtree(DependencyObject start)
        {
            if (start == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(start);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(start, i) as FrameworkElement;
                if (child == null)
                {
                    continue;
                }

                if (TryMenuFromElement(child) != null)
                {
                    return child;
                }

                FrameworkElement nested = FindMenuOwnerInSubtree(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static ListViewItem FindListViewItem(DependencyObject start)
        {
            DependencyObject current = start;
            while (current != null)
            {
                if (current is ListViewItem item)
                {
                    return item;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        /// <summary>
        /// Legacy only: clear template <see cref="UIElement.ContextFlyout"/> so touch Hold
        /// cannot auto-open the MenuFlyout while CommandBar actions open. Mouse still
        /// <see cref="ShowRowContextMenu"/> via the stashed flyout on the <see cref="ListViewItem"/>.
        /// </summary>
        public static void DetachRowContextFlyoutForLegacyHold(FrameworkElement anchor)
        {
            ListViewItem container = FindListViewItem(anchor) ?? anchor as ListViewItem;
            FrameworkElement root = ResolveMenuPlacementRoot(anchor);

            MenuFlyout already = TryGetDetached(container) ?? TryGetDetached(root);
            if (already != null)
            {
                StashDetached(container, already);
                ClearContextFlyout(root);
                ClearContextFlyout(FindMenuOwnerInSubtree(anchor));
                Debug.WriteLine(
                    "[ChatListContext] Detach: already stashed; container=" +
                    (container != null));
                return;
            }

            MenuFlyout flyout = TryMenuFromElement(root) ?? FindMenuInSubtree(anchor);
            if (flyout == null)
            {
                Debug.WriteLine(
                    "[ChatListContext] Detach: no flyout yet; root=" +
                    (root?.GetType().Name ?? "(null)") +
                    "; hasTemplateRoot=" +
                    (container?.ContentTemplateRoot != null));
                return;
            }

            FrameworkElement owner = root;
            if (owner != null && !ReferenceEquals(owner.ContextFlyout, flyout))
            {
                owner = FindMenuOwnerInSubtree(anchor) ?? root;
            }

            StashDetached(container, flyout);
            StashDetached(owner, flyout);
            ClearContextFlyout(owner);
            Debug.WriteLine(
                "[ChatListContext] Detach: stashed on ListViewItem=" + (container != null) +
                "; owner=" + (owner?.GetType().Name ?? "(null)"));
        }

        private static void ClearContextFlyout(FrameworkElement element)
        {
            if (element != null && element.ContextFlyout != null)
            {
                element.ContextFlyout = null;
            }
        }

        /// <summary>
        /// Rows detached under the old Grid-keyed stash can lose the MenuFlyout after recycle.
        /// Inflate a fresh copy from the list's <see cref="ListView.ItemTemplate"/> and stash it
        /// on the ListViewItem (template lives in page resources, not Application.Current).
        /// </summary>
        private static void EnsureRowContextMenuFromTemplate(FrameworkElement anchor)
        {
            ListViewItem container = FindListViewItem(anchor) ?? anchor as ListViewItem;
            if (container == null || TryGetDetached(container) != null)
            {
                return;
            }

            DataTemplate template = null;
            DependencyObject current = container;
            while (current != null && template == null)
            {
                var list = current as ListView;
                if (list != null)
                {
                    template = list.ItemTemplate;
                    break;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            if (template == null)
            {
                Debug.WriteLine("[ChatListContext] Ensure: ItemTemplate missing on ListView");
                return;
            }

            FrameworkElement inflated = null;
            try
            {
                inflated = template.LoadContent() as FrameworkElement;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListContext] Ensure LoadContent failed: " + ex.Message);
                return;
            }

            MenuFlyout flyout = inflated?.ContextFlyout as MenuFlyout
                ?? inflated?.FindName(RowMenuName) as MenuFlyout;
            if (flyout == null)
            {
                Debug.WriteLine("[ChatListContext] Ensure: inflated root has no MenuFlyout");
                return;
            }

            if (inflated != null)
            {
                inflated.ContextFlyout = null;
            }

            StashDetached(container, flyout);
            Debug.WriteLine("[ChatListContext] Ensure: rehydrated MenuFlyout onto ListViewItem");
        }

        /// <summary>
        /// Hides the row menu if it is open (Legacy Hold → CommandBar must not leave a MenuFlyout up).
        /// </summary>
        public static void HideRowContextMenu(FrameworkElement anchor)
        {
            try
            {
                ResolveRowContextMenu(anchor)?.Hide();
            }
            catch
            {
            }
        }

        private static FrameworkElement ResolveTemplateRoot(FrameworkElement anchor)
        {
            if (anchor is ListViewItem container)
            {
                return container.ContentTemplateRoot as FrameworkElement
                    ?? container.Content as FrameworkElement
                    ?? container;
            }

            return anchor;
        }

        /// <summary>
        /// The title for a row when the chat has no name of its own - a group with no subject
        /// yet, or a contact that is still just a number. Lives here because it is a function
        /// binding in this template and a static is the only shape x:Bind accepts.
        /// </summary>
        public static string GetChatDisplayName(string name, ChatKind kind, bool isCommunityAnnounce)
        {
            var item = new ChatItem
            {
                Name = name,
                Kind = kind,
                IsCommunityAnnounce = isCommunityAnnounce
            };
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

        /// <summary>Group glyph under the photo for groups and community hubs.</summary>
        public static bool ShowsGroupAvatar(bool isGroup, ChatKind kind)
        {
            return isGroup || kind == ChatKind.Community;
        }

        /// <summary>
        /// List-row x:Load gate for the standard avatar (not community announcements).
        /// x:Bind cannot invert a bool inline, and Visibility=Collapsed still constructs the control.
        /// </summary>
        public static bool ShowsStandardListAvatar(bool isCommunityAnnounce)
        {
            return !isCommunityAnnounce;
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
            bool liveTiles = ResolveLiveTilesCompatible();
            foreach (var item in flyout.Items)
            {
                var menuItem = item as MenuFlyoutItem;
                var subItem = item as MenuFlyoutSubItem;
                string tag = (menuItem?.Tag as string) ?? (subItem?.Tag as string);

                if (string.Equals(tag, "viewProfile", StringComparison.Ordinal) && menuItem != null)
                {
                    bool canViewProfile = !chat.IsGroup &&
                                          !chat.IsCommunityAnnounce &&
                                          chat.Kind != ChatKind.Community &&
                                          !string.IsNullOrWhiteSpace(chat.JID);
                    menuItem.Visibility = canViewProfile ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "chatPin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.IsChatPinned ? Visibility.Collapsed : Visibility.Visible;
                }
                else if (string.Equals(tag, "chatUnpin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.IsChatPinned ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "addFavorite", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.IsFavorite ? Visibility.Collapsed : Visibility.Visible;
                }
                else if (string.Equals(tag, "removeFavorite", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "widgetPin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = liveTiles && !chat.IsWidgetPinned
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "widgetUnpin", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = liveTiles && chat.IsWidgetPinned
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "localMuteSub", StringComparison.Ordinal) && subItem != null)
                {
                    subItem.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
                    subItem.Foreground = new SolidColorBrush(Windows.UI.Colors.White);
                }
                else if (string.Equals(tag, "unmute", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "addContact", StringComparison.Ordinal) && menuItem != null)
                {
                    bool canAdd = false;
                    try
                    {
                        canAdd = App.Services?.GetService<IContactService>()?.CanAddToAddressBook(chat.JID) == true;
                    }
                    catch
                    {
                    }

                    menuItem.Visibility = canAdd ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "archiveChat", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.Status == ChatStatus.Active
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
                else if (string.Equals(tag, "unarchiveChat", StringComparison.Ordinal) && menuItem != null)
                {
                    menuItem.Visibility = chat.Status == ChatStatus.Archived
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }
        }

        private void ViewProfile_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            if (chat != null)
            {
                FindChatList(sender)?.ViewProfile(chat);
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

        private async void FavoriteChat_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            if (chat == null)
            {
                return;
            }

            IChatService chats = null;
            try
            {
                chats = App.Services?.GetService<IChatService>();
            }
            catch
            {
            }

            if (chats == null)
            {
                return;
            }

            try
            {
                await chats.SetFavoriteAsync(chat, !chat.IsFavorite);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatItemTemplates] Favorite failed: " + ex.Message);
            }
        }

        private void PinToStart_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            FindChatList(sender)?.PinChatToStart(chat);
        }

        private async void AddContact_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            IContactService contacts = null;
            try
            {
                contacts = App.Services?.GetService<IContactService>();
            }
            catch
            {
            }

            if (chat == null || contacts == null)
            {
                return;
            }

            IStringResources strings = null;
            try
            {
                strings = App.Services?.GetService<IStringResources>();
            }
            catch
            {
            }

            try
            {
                await contacts.ShowAddToAddressBookAsync(
                    chat.GetNameResolved(strings),
                    contacts.TryResolvePhone(chat.JID),
                    chat.GetAvatarUrl(preferHigh: true),
                    chat.JID);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChatItemTemplates] Add contact failed: " + ex.Message);
            }
        }

        private void MuteDuration_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var chat = ResolveChat(element);
            string tag = element?.Tag as string;
            IChatService chats = null;
            try
            {
                chats = App.Services?.GetService<IChatService>();
            }
            catch
            {
            }

            if (chats == null || chat == null)
            {
                return;
            }

            long? until = null;
            if (string.Equals(tag, "mute8h", StringComparison.Ordinal))
            {
                until = chats.MuteUntilEightHoursFromNow();
            }
            else if (string.Equals(tag, "mute1w", StringComparison.Ordinal))
            {
                until = chats.MuteUntilOneWeekFromNow();
            }
            else if (string.Equals(tag, "muteForever", StringComparison.Ordinal))
            {
                until = chats.MuteForeverUnixSeconds;
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

        private void DeleteChat_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            if (chat != null)
            {
                FindChatList(sender)?.DeleteChat(chat);
            }
        }

        private void ArchiveChat_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            FindChatList(sender)?.ArchiveChat(chat);
        }

        private void UnarchiveChat_Click(object sender, RoutedEventArgs e)
        {
            var chat = ResolveChat(sender as FrameworkElement);
            FindChatList(sender)?.UnarchiveChat(chat);
        }

        private static ChatItem ResolveChat(FrameworkElement element)
        {
            var chat = element?.DataContext as ChatItem;
            if (chat != null)
            {
                return chat;
            }

            // Walk up from submenu items to the root MenuFlyout target (feature/2.1).
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
        private static IChatRowActions FindChatList(object sender)
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
                        var fromTarget = FindAncestor<IChatRowActions>(flyout.Target);
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

            var fromSender = FindAncestor<IChatRowActions>(sender as DependencyObject);
            if (fromSender != null)
            {
                return fromSender;
            }

            return FindInSubtree<IChatRowActions>(Window.Current?.Content as DependencyObject);
        }

        private static bool ResolveLiveTilesCompatible()
        {
            try
            {
                var provider = App.Services?.GetService<ISystemInfoProvider>();
                if (provider != null)
                {
                    return provider.LiveTilesCompatible();
                }
            }
            catch
            {
            }

            return SystemInfoProvider.DetectLiveTilesCompatible();
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
