using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Unison.Uwp.Services;
using Unison.Uwp.UI.Templates;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    public class ChatSelectedEventArgs : EventArgs
    {
        public ChatItem SelectedChat { get; }
        public ChatSelectedEventArgs(ChatItem chat) => SelectedChat = chat;
    }

    /// <summary>
    /// The chat list control.
    /// </summary>
    /// <remarks>
    /// Which conversations are shown, in what order, and what the header says while syncing all
    /// belong to <see cref="ChatListViewModel"/> and are bound from the markup. What is left here
    /// is the part a view model cannot do:
    /// <list type="bullet">
    /// <item>the ListView selection, which has to be written to the control and which reports
    /// transient nulls while the list is being rebuilt - never a real deselection;</item>
    /// <item>row realization, so avatars are only fetched for rows that reached the screen;</item>
    /// <item>clipping the header text to its column;</item>
    /// <item>reaching the surrounding <see cref="ChatsView"/>, which lives in the visual tree.</item>
    /// </list>
    /// </remarks>
    public sealed partial class ChatListView : UserControl, IChatRowActions
    {
        public ChatListViewModel ViewModel { get; private set; }

        /// <summary>A conversation is open. Raised for re-binds too: after a PN/LID merge the same
        /// conversation is a different <see cref="ChatItem"/> instance.</summary>
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;

        public event EventHandler MenuClicked;

        /// <summary>Shell owns search-state chrome; list CommandBar only raises the request.</summary>
        public event EventHandler SearchRequested;

        public event EventHandler NewChatRequested;

        public event EventHandler ProfileRequested;

        public event EventHandler DebugRequested;

        public event EventHandler SettingsRequested;

        private ShellViewModel _shell;
        private bool _hooked;

        /// <summary>
        /// Parent page must call <see cref="ConfigureScope"/> before we subscribe/attach.
        /// On phone, Loaded can run synchronously inside InitializeComponent — Attach before
        /// scope is set used to throw and tear down navigation (Archived on Creators Update).
        /// </summary>
        private bool _scopeReady;

        /// <summary>
        /// Set while we are the ones writing the selection. Without it, every programmatic
        /// selection would come back through <see cref="ChatList_SelectionChanged"/> and be
        /// reported to the shell as if the user had clicked.
        /// </summary>
        private bool _suppressSelectionChanged;
        private int _containerChangingCount;

        public ChatListView()
            : this(null, null)
        {
        }

        public ChatListView(ChatListViewModel viewModel, ShellViewModel shell)
        {
            ViewModel = viewModel ?? App.Services?.GetRequiredService<ChatListViewModel>();
            _shell = shell ?? App.Services?.GetService<ShellViewModel>();
            DataContext = ViewModel;
            this.InitializeComponent();
            this.Loaded += ChatListView_Loaded;
            this.Unloaded += ChatListView_Unloaded;
        }

        public void ConfigureScope(ChatListScope scope)
        {
            ViewModel?.ConfigureScope(scope);
            ApplyScopeChrome(scope);
            _scopeReady = true;
            TryAttach();
        }

        private void ApplyScopeChrome(ChatListScope scope)
        {
            if (FilterButton == null)
            {
                return;
            }

            FilterButton.Visibility = scope == ChatListScope.Archived
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void ChatListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                _shell.PropertyChanged += Shell_PropertyChanged;
            }

            ApplyFilterLabel();
            RefreshProfileSecondary();
            TryAttach();
        }

        private void ApplyFilterLabel()
        {
            if (FilterLabel == null)
            {
                return;
            }

            var strings = App.Services?.GetService<IStringResources>();
            FilterLabel.Text = strings?.Get("Shell_LegacyAppBar_Filter.Text", "Filter Messages")
                ?? "Filter Messages";
        }

        /// <summary>
        /// Stock MenuFlyout only — ModernMenuFlyout* templates historically used Grid.CornerRadius
        /// (1809+) and XAML-parse-crash Creators Update when the chats list loads.
        /// </summary>
        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterButton == null || ViewModel?.FilterChatsCommand == null)
            {
                return;
            }

            var strings = App.Services?.GetService<IStringResources>();
            var flyout = new MenuFlyout();
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterAll.Text", "All") ?? "All", 0));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterUnread.Text", "Unread") ?? "Unread", 1));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterFavorites.Text", "Favorites") ?? "Favorites", 2));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterContacts.Text", "Contacts") ?? "Contacts", 3));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterNonContacts.Text", "Non-contacts") ?? "Non-contacts", 4));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterGroups.Text", "Groups") ?? "Groups", 5));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterDrafts.Text", "Drafts") ?? "Drafts", 6));
            flyout.ShowAt(FilterButton);
        }

        private MenuFlyoutItem CreateFilterItem(string text, int filter)
        {
            return new MenuFlyoutItem
            {
                Text = text,
                Command = ViewModel.FilterChatsCommand,
                CommandParameter = filter
            };
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.CurrentUserName) ||
                e.PropertyName == nameof(ShellViewModel.CurrentUserAvatar))
            {
                RefreshProfileSecondary();
            }
        }

        private void ListCommandBar_Loaded(object sender, RoutedEventArgs e)
        {
            CenterPrimaryCommands(ListCommandBar);
        }

        private static void CenterPrimaryCommands(CommandBar bar)
        {
            LegacyCommandBarLayout.CenterPrimaryCommands(bar);
        }

        private void SearchAppBar_Click(object sender, RoutedEventArgs e) =>
            SearchRequested?.Invoke(this, EventArgs.Empty);

        private void NewChatAppBar_Click(object sender, RoutedEventArgs e) =>
            NewChatRequested?.Invoke(this, EventArgs.Empty);

        private void ProfileAppBar_Click(object sender, RoutedEventArgs e) =>
            ProfileRequested?.Invoke(this, EventArgs.Empty);

        private void DebugAppBar_Click(object sender, RoutedEventArgs e) =>
            DebugRequested?.Invoke(this, EventArgs.Empty);

        private void SettingsAppBar_Click(object sender, RoutedEventArgs e) =>
            SettingsRequested?.Invoke(this, EventArgs.Empty);

        private void RefreshProfileSecondary() =>
            LegacyProfileSecondary.Apply(ProfileSecondaryButton, _shell);

        private void TryAttach()
        {
            if (!_scopeReady || _hooked || ViewModel == null)
            {
                return;
            }

            _hooked = true;
            ViewModel.MenuRequested += ViewModel_MenuRequested;
            ViewModel.OpenChatRequested += ViewModel_OpenChatRequested;
            ViewModel.BeforeLocalConversationsCleared += ViewModel_BeforeLocalConversationsCleared;
            ViewModel.SelectionRestored += ViewModel_SelectionRestored;
            ViewModel.Attach();
        }

        private void ChatListView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_shell != null)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
            }

            if (ViewModel == null || !_hooked)
            {
                return;
            }

            _hooked = false;
            _scopeReady = false;
            ViewModel.MenuRequested -= ViewModel_MenuRequested;
            ViewModel.OpenChatRequested -= ViewModel_OpenChatRequested;
            ViewModel.BeforeLocalConversationsCleared -= ViewModel_BeforeLocalConversationsCleared;
            ViewModel.SelectionRestored -= ViewModel_SelectionRestored;
            ViewModel.Detach();
        }

        // ---------------------------------------------------------------------
        // Selection
        // ---------------------------------------------------------------------

        private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged || ViewModel == null || ViewModel.IsRefreshing)
            {
                return;
            }

            var chat = ChatList.SelectedItem as ChatItem;
            if (chat == null)
            {
                // A rebuild, a Move or a dedupe can leave the ListView pointing at nothing for an
                // instant. Closing the open conversation on that would be wrong, so put the
                // selection back where it was instead of reporting a deselection.
                var restored = ViewModel.ResolveLastSelection();
                if (restored != null)
                {
                    SetSelectionQuiet(restored);
                }

                return;
            }

            ViewModel.OnChatSelected(chat);
            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
        }

        private void ViewModel_SelectionRestored(object sender, ChatItem chat)
        {
            SetSelectionQuiet(chat);
            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
        }

        private void SetSelectionQuiet(ChatItem chat)
        {
            if (ChatList == null || ReferenceEquals(ChatList.SelectedItem, chat))
            {
                return;
            }

            bool previous = _suppressSelectionChanged;
            _suppressSelectionChanged = true;
            try
            {
                ChatList.SelectedItem = chat;
            }
            finally
            {
                _suppressSelectionChanged = previous;
            }
        }

        /// <summary>Selects a chat without raising <see cref="ChatSelected"/> (the caller opens the detail itself).</summary>
        internal void HighlightChatQuiet(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            ViewModel?.EnsureVisible(chat);
            SetSelectionQuiet(chat);
        }

        /// <summary>Drops the selection on purpose - back navigation, or a wipe.</summary>
        public void ClearSelection()
        {
            ViewModel?.ClearSelection();
            SetSelectionQuiet(null);
        }

        /// <summary>Finds a chat by JID or canonical id, for callers holding a stale instance.</summary>
        internal ChatItem FindChatByJid(string jid) => ViewModel?.FindChatByJid(jid);

        // ---------------------------------------------------------------------
        // Rows
        // ---------------------------------------------------------------------

        /// <summary>x:Load just created the ListView after first-sync hold released.</summary>
        private void ChatList_Loaded(object sender, RoutedEventArgs e)
        {
            int items = 0;
            try
            {
                items = ChatList?.Items?.Count ?? 0;
            }
            catch
            {
            }

            TraceListUi(
                "listview-loaded",
                "items=" + items +
                "; visibleVm=" + (ViewModel?.VisibleChats?.Count ?? 0) +
                "; materialized=" + (ViewModel?.IsChatListMaterialized ?? false),
                flush: true);
        }

        private void ChatList_Unloaded(object sender, RoutedEventArgs e)
        {
            TraceListUi(
                "listview-unloaded",
                "visibleVm=" + (ViewModel?.VisibleChats?.Count ?? 0),
                flush: true);
        }

        private void ChatList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args == null || args.InRecycleQueue || args.Item == null)
            {
                return;
            }

            _containerChangingCount++;
            if (_containerChangingCount <= 5 || (_containerChangingCount % 25) == 0)
            {
                TraceListUi(
                    "container-changing",
                    "n=" + _containerChangingCount +
                    "; phase=" + args.Phase +
                    "; visibleVm=" + (ViewModel?.VisibleChats?.Count ?? 0),
                    flush: _containerChangingCount <= 3);
            }

            // On demand, and only for rows that actually reached the screen. Fetching every
            // avatar up front cost hundreds of requests for rows nobody scrolled to.
            try
            {
                ViewModel?.OnRowRealized(args.Item as ChatItem);
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "chat-list-ui",
                    "row-realized-failed",
                    ex,
                    "n=" + _containerChangingCount);
                _ = RuntimeDiagnosticsService.Instance.FlushAsync("chat-list-ui:row-realized-failed");
            }
        }

        private static void TraceListUi(string eventName, string details, bool flush)
        {
            try
            {
                string mem = "gcMb=" + (GC.GetTotalMemory(false) / (1024 * 1024));
                RuntimeDiagnosticsService.Instance.Write(
                    "chat-list-ui",
                    eventName,
                    string.IsNullOrWhiteSpace(details) ? mem : details + "; " + mem);
                if (flush)
                {
                    _ = RuntimeDiagnosticsService.Instance.FlushAsync("chat-list-ui:" + eventName);
                }
            }
            catch
            {
            }
        }

        /// <summary>Context flyout on a row: pin the conversation as a Start tile.</summary>
        public void PinChatToStart(ChatItem chat)
        {
            if (chat == null || ViewModel?.PinChatToStartCommand == null)
            {
                return;
            }

            if (ViewModel.PinChatToStartCommand.CanExecute(chat))
            {
                ViewModel.PinChatToStartCommand.Execute(chat);
            }
        }

        /// <summary>Context flyout on a row: pin the conversation for the whole account.</summary>
        public void SetChatPinned(ChatItem chat, bool pinned)
        {
            if (chat == null || ViewModel?.SetChatPinnedCommand == null)
            {
                return;
            }

            var request = new ChatPinRequest { Chat = chat, Pinned = pinned };
            if (ViewModel.SetChatPinnedCommand.CanExecute(request))
            {
                ViewModel.SetChatPinnedCommand.Execute(request);
            }
        }

        /// <summary>Context flyout on a row: delete the conversation (asks first).</summary>
        public void DeleteChat(ChatItem chat)
        {
            if (chat == null || ViewModel?.DeleteChatCommand == null)
            {
                return;
            }

            if (ViewModel.DeleteChatCommand.CanExecute(chat))
            {
                ViewModel.DeleteChatCommand.Execute(chat);
            }
        }

        public void ArchiveChat(ChatItem chat)
        {
            if (chat == null || ViewModel?.ArchiveChatCommand == null)
            {
                return;
            }

            if (ViewModel.ArchiveChatCommand.CanExecute(chat))
            {
                ViewModel.ArchiveChatCommand.Execute(chat);
            }
        }

        public void UnarchiveChat(ChatItem chat)
        {
            if (chat == null || ViewModel?.UnarchiveChatCommand == null)
            {
                return;
            }

            if (ViewModel.UnarchiveChatCommand.CanExecute(chat))
            {
                ViewModel.UnarchiveChatCommand.Execute(chat);
            }
        }

        /// <summary>Context flyout on a row: mute until a moment, or unmute with null.</summary>
        public void SetLocalMute(ChatItem chat, long? mutedUntilUnixSeconds)
        {
            if (chat == null || ViewModel?.SetLocalMuteCommand == null)
            {
                return;
            }

            var request = mutedUntilUnixSeconds.HasValue
                ? ChatMuteRequest.Mute(chat, mutedUntilUnixSeconds.Value)
                : ChatMuteRequest.Unmute(chat);

            if (ViewModel.SetLocalMuteCommand.CanExecute(request))
            {
                ViewModel.SetLocalMuteCommand.Execute(request);
            }
        }

        // ---------------------------------------------------------------------
        // Header
        // ---------------------------------------------------------------------

        private void ViewModel_MenuRequested(object sender, EventArgs e)
        {
            Debug.WriteLine("[ChatListView] MenuRequested → MenuClicked");
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Direct path so the shell hamburger still opens if Command/MenuRequested was unhooked
            // after Settings navigation (UWP Unloaded/Loaded races).
            Debug.WriteLine("[ChatListView] MenuButton_Click");
            MenuClicked?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// UWP TextBlock does not clip overflow into adjacent Grid columns; long sync strings
        /// painted over header icons. Clip to arranged bounds + CharacterEllipsis.
        /// Clear Clip when collapsed so a stale zero-rect does not stick across refresh.
        /// </summary>
        private void SyncStatusPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (SyncStatusPanel == null)
            {
                return;
            }

            if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0 ||
                SyncStatusPanel.Visibility != Visibility.Visible)
            {
                SyncStatusPanel.Clip = null;
                return;
            }

            SyncStatusPanel.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
        }

        // ---------------------------------------------------------------------
        // Shell coordination
        // ---------------------------------------------------------------------

        // Raised by the view model rather than by XAML, so async void here has nothing above it to
        // catch an escape. It also waits up to a quarter second before touching the list, and the
        // view can be unloaded by then — unsubscribing does not recall a call already in flight.
        private async void ViewModel_OpenChatRequested(object sender, string jid)
        {
            try
            {
                if (string.IsNullOrEmpty(jid) || ViewModel == null)
                {
                    return;
                }

                // A new chat may still be materializing in the store; give it a short chance.
                ChatItem chat = null;
                for (int i = 0; i < 5 && chat == null; i++)
                {
                    chat = ViewModel.FindChatByJid(jid);
                    if (chat == null)
                    {
                        await Task.Delay(50);
                    }
                }

                if (chat == null || ChatList == null || ViewModel == null)
                {
                    return;
                }

                // Deliberately not quiet: opening a new chat should behave like picking it.
                ViewModel.EnsureVisible(chat);
                ChatList.SelectedItem = chat;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format("[ChatListView] Opening a chat on request failed: {0}", ex.Message));
            }
        }

        private void ViewModel_BeforeLocalConversationsCleared(object sender, EventArgs e)
        {
            SetSelectionQuiet(null);

            // Leave NarrowDetail empty-state; restore list pane during wipe/resync.
            try
            {
                FindAncestorConversationShellPage()?.NotifyLocalConversationsCleared();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListView] NotifyLocalConversationsCleared: " + ex.Message);
            }
        }

        private UI.Views.IConversationShellPage FindAncestorConversationShellPage()
        {
            DependencyObject current = this;
            while (current != null)
            {
                var match = current as UI.Views.IConversationShellPage;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return Window.Current?.Content != null
                ? FindInSubtree<UI.Views.IConversationShellPage>(Window.Current.Content as DependencyObject)
                : null;
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
