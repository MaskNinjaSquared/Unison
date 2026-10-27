using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Uwp.UI.Helpers;
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

        /// <summary>
        /// Hub back (header or system): selection was cleared with the hub — detail must close too.
        /// </summary>
        public event EventHandler CommunityHubClosed;

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

        /// <summary>
        /// Mouse right-click must not open the chat via <see cref="ChatList_SelectionChanged"/>.
        /// </summary>
        private bool _ignoreNextSelectionFromContext;
        private int _containerChangingCount;

        /// <summary>Same gate as message bubbles: Hold must not also open the desktop MenuFlyout.</summary>
        private readonly MessageBubbleContextGestureGate _rowContextGate =
            new MessageBubbleContextGestureGate();

        private ChatItem _actionsChat;

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
            bool archivedOrChannels =
                scope == ChatListScope.Archived || scope == ChatListScope.Channels;
            bool channelsOnly = scope == ChatListScope.Channels;

            if (FilterButton != null)
            {
                FilterButton.Visibility = archivedOrChannels
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            // Channels: bottom bar is search only (no new chat / add contact / overflow).
            Visibility composeVisibility = channelsOnly
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (NewChatAppBarButton != null)
            {
                NewChatAppBarButton.Visibility = composeVisibility;
            }

            if (AddContactAppBarButton != null)
            {
                AddContactAppBarButton.Visibility = composeVisibility;
            }

            if (ListCommandBar != null)
            {
                if (channelsOnly)
                {
                    ListCommandBar.SecondaryCommands.Clear();
                }

                CenterPrimaryCommands(ListCommandBar);
            }
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
            string fallback = strings?.Get("Shell_LegacyAppBar_Filter.Text", "Filter Messages")
                ?? "Filter Messages";
            if (ViewModel == null || !ViewModel.IsFilterActive)
            {
                FilterLabel.Text = fallback;
                return;
            }

            FilterLabel.Text = ResolveActiveFilterLabel(strings) ?? fallback;
        }

        private string ResolveActiveFilterLabel(IStringResources strings)
        {
            if (ViewModel == null)
            {
                return null;
            }

            switch (ViewModel.ActiveFilter)
            {
                case ChatListFilter.Unread:
                    return strings?.Get("ChatList_FilterUnread.Text", "Unread") ?? "Unread";
                case ChatListFilter.Favorites:
                    return strings?.Get("ChatList_FilterFavorites.Text", "Favorites") ?? "Favorites";
                case ChatListFilter.Contacts:
                    return strings?.Get("ChatList_FilterContacts.Text", "Contacts") ?? "Contacts";
                case ChatListFilter.NonContacts:
                    return strings?.Get("ChatList_FilterNonContacts.Text", "Non-contacts") ?? "Non-contacts";
                case ChatListFilter.Groups:
                    return strings?.Get("ChatList_FilterGroups.Text", "Groups") ?? "Groups";
                case ChatListFilter.Communities:
                    return strings?.Get("ChatList_FilterCommunities.Text", "Communities") ?? "Communities";
                case ChatListFilter.Drafts:
                    return strings?.Get("ChatList_FilterDrafts.Text", "Drafts") ?? "Drafts";
                default:
                    return null;
            }
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
                strings?.Get("ChatList_FilterCommunities.Text", "Communities") ?? "Communities", 7));
            flyout.Items.Add(CreateFilterItem(
                strings?.Get("ChatList_FilterDrafts.Text", "Drafts") ?? "Drafts", 6));
            flyout.ShowAt(FilterButton);
        }

        private MenuFlyoutItem CreateFilterItem(string text, int filter)
        {
            // Click (not Command): on older UWP builds MenuFlyoutItem.CommandParameter boxing
            // against RelayCommand<int> CanExecute was flaky and left every item inert.
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) =>
            {
                var command = ViewModel?.FilterChatsCommand;
                if (command != null && command.CanExecute(filter))
                {
                    command.Execute(filter);
                }
            };
            return item;
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
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
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
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.Detach();
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ChatListViewModel.ActiveCommunity) ||
                e.PropertyName == nameof(ChatListViewModel.IsCommunityHubOpen) ||
                e.PropertyName == nameof(ChatListViewModel.SelectedChat))
            {
                SyncCommunityHubSelection();
            }

            if (e.PropertyName == nameof(ChatListViewModel.ActiveFilter) ||
                e.PropertyName == nameof(ChatListViewModel.IsFilterActive))
            {
                ApplyFilterLabel();
            }
        }

        // ---------------------------------------------------------------------
        // Selection
        // ---------------------------------------------------------------------

        private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Right-button press selects the row before RightTapped; do not open the chat.
            if (_ignoreNextSelectionFromContext || IsRightMouseButtonDown())
            {
                Debug.WriteLine(
                    "[ChatListContext] SelectionChanged ignored (right-click); flag=" +
                    _ignoreNextSelectionFromContext +
                    "; rightDown=" + IsRightMouseButtonDown());
                _ignoreNextSelectionFromContext = false;
                ChatItem keep = e.RemovedItems != null && e.RemovedItems.Count > 0
                    ? e.RemovedItems[0] as ChatItem
                    : ViewModel?.ResolveLastSelection();
                SetSelectionQuiet(keep);
                return;
            }

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

            DismissChatActions();

            if (ViewModel.TryOpenCommunityHub(chat))
            {
                SetSelectionQuiet(null);
                SyncCommunityHubSelection();
                return;
            }

            ViewModel.OnChatSelected(chat);
            if (ViewModel.IsCommunityHubOpen)
            {
                SetSelectionQuiet(null);
                SyncCommunityHubSelection();
            }

            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
        }

        private void CommunityHubList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ignoreNextSelectionFromContext || IsRightMouseButtonDown())
            {
                _ignoreNextSelectionFromContext = false;
                ChatItem keep = e.RemovedItems != null && e.RemovedItems.Count > 0
                    ? e.RemovedItems[0] as ChatItem
                    : null;
                if (sender is ListView hub)
                {
                    bool previous = _suppressSelectionChanged;
                    _suppressSelectionChanged = true;
                    hub.SelectedItem = keep;
                    _suppressSelectionChanged = previous;
                }

                return;
            }

            if (_suppressSelectionChanged || ViewModel == null)
            {
                return;
            }

            var list = sender as ListView;
            var group = list?.SelectedItem as ChatItem;
            if (group == null)
            {
                return;
            }

            ViewModel.SelectCommunityGroup(group);
            // Keep hub open and this row selected (do not clear SelectedItem).
            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(group));
        }

        private void ViewModel_SelectionRestored(object sender, ChatItem chat)
        {
            if (chat != null &&
                !string.IsNullOrWhiteSpace(chat.LinkedParentJid) &&
                ViewModel?.EnsureCommunityContext(chat) == true)
            {
                SetSelectionQuiet(null);
                SyncCommunityHubSelection();
            }
            else
            {
                SetSelectionQuiet(chat);
            }

            ChatSelected?.Invoke(this, new ChatSelectedEventArgs(chat));
        }

        private void SyncCommunityHubSelection()
        {
            if (CommunityHubList == null || ViewModel == null || !ViewModel.IsCommunityHubOpen)
            {
                return;
            }

            ChatItem wanted = ViewModel.SelectedChat;
            ChatItem match = null;
            if (wanted != null && ViewModel.ActiveCommunity?.CommunityMembers != null)
            {
                string jid = wanted.JID ?? string.Empty;
                foreach (ChatItem member in ViewModel.ActiveCommunity.CommunityMembers)
                {
                    if (member == null)
                    {
                        continue;
                    }

                    if (ReferenceEquals(member, wanted) ||
                        string.Equals(member.JID, jid, StringComparison.OrdinalIgnoreCase))
                    {
                        match = member;
                        break;
                    }
                }
            }

            if (ReferenceEquals(CommunityHubList.SelectedItem, match))
            {
                return;
            }

            bool previous = _suppressSelectionChanged;
            _suppressSelectionChanged = true;
            try
            {
                CommunityHubList.SelectedItem = match;
            }
            finally
            {
                _suppressSelectionChanged = previous;
            }
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

            if (ViewModel != null &&
                !string.IsNullOrWhiteSpace(chat.LinkedParentJid) &&
                ViewModel.EnsureCommunityContext(chat))
            {
                ViewModel.OnChatSelected(chat);
                SetSelectionQuiet(null);
                SyncCommunityHubSelection();
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

        /// <summary>Closes the community hub when open; used by system Back before leaving the list.</summary>
        public bool TryConsumeCommunityHubBack()
        {
            if (ViewModel?.IsCommunityHubOpen != true)
            {
                return false;
            }

            CloseCommunityHubFromUi();
            return true;
        }

        private void CommunityHubBackButton_Click(object sender, RoutedEventArgs e)
        {
            TryConsumeCommunityHubBack();
        }

        /// <summary>Leaves the hub, clears the member highlight, and asks the shell to close detail.</summary>
        private void CloseCommunityHubFromUi()
        {
            ViewModel?.CloseCommunityHub();
            SetSelectionQuiet(null);
            CommunityHubClosed?.Invoke(this, EventArgs.Empty);
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
            HandleLegacyRowContainerChanging(args, trace: true);
        }

        private void CommunityHubList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            HandleLegacyRowContainerChanging(args, trace: false);
        }

        private void HandleLegacyRowContainerChanging(ContainerContentChangingEventArgs args, bool trace)
        {
            if (args == null)
            {
                return;
            }

            var container = args.ItemContainer as ListViewItem;
            if (container == null)
            {
                return;
            }

            // Hold → CommandBar. Detach template Grid.ContextFlyout so the platform cannot
            // auto-open MenuFlyout on Mobile Hold (Hide() races and loses). Mouse ShowAt
            // uses the stashed flyout (ChatItemTemplates.DetachRowContextFlyoutForLegacyHold).
            container.ContextFlyout = null;
            ChatItemTemplates.DetachRowContextFlyoutForLegacyHold(container);
            container.IsHoldingEnabled = true;
            container.IsRightTapEnabled = true;
            container.Holding -= ChatRow_Holding;
            container.PointerPressed -= ChatRow_PointerPressed;
            container.RightTapped -= ChatRow_RightTapped;
            container.ContextRequested -= ChatRow_ContextRequested;

            if (args.InRecycleQueue || args.Item == null)
            {
                return;
            }

            // Recycled roots may get a fresh template with ContextFlyout again.
            ChatItemTemplates.DetachRowContextFlyoutForLegacyHold(container);
            // Phase 0 often runs before ContentTemplateRoot exists — detach again once realized.
            if (container.ContentTemplateRoot == null && args.Phase == 0)
            {
                args.RegisterUpdateCallback((s, e) =>
                {
                    var late = e.ItemContainer as ListViewItem;
                    if (late != null && e.Item != null)
                    {
                        ChatItemTemplates.DetachRowContextFlyoutForLegacyHold(late);
                    }
                });
            }

            container.Holding += ChatRow_Holding;
            container.PointerPressed += ChatRow_PointerPressed;
            container.RightTapped += ChatRow_RightTapped;
            container.ContextRequested += ChatRow_ContextRequested;

            if (trace)
            {
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

        private void ChatRow_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started)
            {
                return;
            }

            // Mouse right-click must open MenuFlyout, not the Legacy CommandBar.
            if (e.PointerDeviceType == PointerDeviceType.Mouse)
            {
                Debug.WriteLine("[ChatListContext] Holding ignored (mouse)");
                return;
            }

            e.Handled = true;
            if (!_rowContextGate.TryBeginFromHold())
            {
                Debug.WriteLine("[ChatListContext] Holding suppressed by gate");
                return;
            }

            var container = sender as ListViewItem;
            ChatItemTemplates.HideRowContextMenu(container);
            Debug.WriteLine(
                "[ChatListContext] Holding → CommandBar chat=" +
                (ResolveContainerChat(container)?.JID ?? "(null)"));
            OpenChatActions(ResolveContainerChat(container));
        }

        /// <summary>
        /// ListView selects on PointerPressed (including right button) before RightTapped /
        /// ContextRequested. Arm the selection ignore here without marking Handled — Handled
        /// would kill RightTapped and the menu would never open.
        /// </summary>
        private void ChatList_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ArmIgnoreSelectionIfRightButton(e, "list");
        }

        private void ChatRow_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            ArmIgnoreSelectionIfRightButton(e, "row");
        }

        private void ArmIgnoreSelectionIfRightButton(PointerRoutedEventArgs e, string source)
        {
            if (e == null)
            {
                return;
            }

            try
            {
                if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
                {
                    _ignoreNextSelectionFromContext = true;
                    Debug.WriteLine(
                        "[ChatListContext] PointerPressed right → ignoreSelection=true (" +
                        source + ")");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListContext] PointerPressed arm failed: " + ex.Message);
            }
        }

        /// <summary>
        /// True while the mouse right button is held — SelectionChanged often runs during
        /// PointerPressed before our handler can arm <see cref="_ignoreNextSelectionFromContext"/>.
        /// </summary>
        private static bool IsRightMouseButtonDown()
        {
            try
            {
                CoreWindow window = Window.Current?.CoreWindow;
                if (window == null)
                {
                    return false;
                }

                CoreVirtualKeyStates state = window.GetKeyState(VirtualKey.RightButton);
                return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Desktop mouse only. Touch long-press uses Holding → CommandBar (RightTapped echo is ignored).
        /// </summary>
        private void ChatList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            Debug.WriteLine(
                "[ChatListContext] List RightTapped; device=" +
                (e == null ? "(null)" : e.PointerDeviceType.ToString()) +
                "; handled=" + (e != null && e.Handled));
            HandleMouseRightTap(
                e,
                e == null ? null : FindListViewItem(e.OriginalSource as DependencyObject),
                container => e.GetPosition(container));
        }

        private void ChatRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            Debug.WriteLine(
                "[ChatListContext] Row RightTapped; device=" +
                (e == null ? "(null)" : e.PointerDeviceType.ToString()) +
                "; handled=" + (e != null && e.Handled) +
                "; sender=" + (sender?.GetType().Name ?? "(null)"));
            HandleMouseRightTap(e, sender as ListViewItem, container => e.GetPosition(container));
        }

        private void ChatList_ContextRequested(object sender, ContextRequestedEventArgs e)
        {
            Debug.WriteLine(
                "[ChatListContext] List ContextRequested; handled=" + (e != null && e.Handled));
            HandleMouseContextRequested(
                e,
                FindListViewItem(e.OriginalSource as DependencyObject));
        }

        private void ChatRow_ContextRequested(object sender, ContextRequestedEventArgs e)
        {
            Debug.WriteLine(
                "[ChatListContext] Row ContextRequested; handled=" + (e != null && e.Handled) +
                "; sender=" + (sender?.GetType().Name ?? "(null)"));
            HandleMouseContextRequested(e, sender as ListViewItem);
        }

        private void HandleMouseRightTap(
            RightTappedRoutedEventArgs e,
            ListViewItem container,
            Func<ListViewItem, Point> position)
        {
            if (e == null)
            {
                Debug.WriteLine("[ChatListContext] RightTap skip: e null");
                return;
            }

            if (e.Handled)
            {
                Debug.WriteLine("[ChatListContext] RightTap skip: already Handled");
                return;
            }

            if (e.PointerDeviceType != PointerDeviceType.Mouse)
            {
                Debug.WriteLine(
                    "[ChatListContext] RightTap skip: device=" + e.PointerDeviceType);
                return;
            }

            if (container == null)
            {
                Debug.WriteLine("[ChatListContext] RightTap skip: no ListViewItem");
                return;
            }

            if (!TryShowRowContextMenu(container, position(container)))
            {
                Debug.WriteLine("[ChatListContext] RightTap TryShow failed");
                return;
            }

            e.Handled = true;
            Debug.WriteLine("[ChatListContext] RightTap menu shown");
        }

        private void HandleMouseContextRequested(ContextRequestedEventArgs e, ListViewItem container)
        {
            if (e == null || e.Handled)
            {
                Debug.WriteLine("[ChatListContext] ContextRequested skip: null/handled");
                return;
            }

            if (container == null)
            {
                Debug.WriteLine("[ChatListContext] ContextRequested skip: no ListViewItem");
                return;
            }

            Point at = new Point(double.NaN, double.NaN);
            e.TryGetPosition(container, out at);
            if (!TryShowRowContextMenu(container, at))
            {
                Debug.WriteLine("[ChatListContext] ContextRequested TryShow failed");
                return;
            }

            e.Handled = true;
            Debug.WriteLine("[ChatListContext] ContextRequested menu shown");
        }

        private bool TryShowRowContextMenu(ListViewItem container, Point position)
        {
            ChatItem chat = ResolveContainerChat(container);
            if (container == null || chat == null)
            {
                Debug.WriteLine(
                    "[ChatListContext] TryShow abort: container=" +
                    (container != null) + "; chat=" + (chat != null));
                return false;
            }

            if (ChatItemTemplates.ResolveRowContextMenu(container) == null)
            {
                Debug.WriteLine(
                    "[ChatListContext] TryShow abort: ResolveRowContextMenu null; jid=" +
                    (chat.JID ?? "?"));
                return false;
            }

            if (!_rowContextGate.TryBeginFromRightTap())
            {
                Debug.WriteLine(
                    "[ChatListContext] TryShow abort: gesture gate blocked; jid=" +
                    (chat.JID ?? "?"));
                return false;
            }

            // Belt-and-suspenders: PointerPressed usually armed this already; SelectionChanged
            // may have consumed it when the pressed row was already selected.
            _ignoreNextSelectionFromContext = true;
            if (!ChatItemTemplates.TryShowRowContextMenu(container, position))
            {
                _ignoreNextSelectionFromContext = false;
                Debug.WriteLine(
                    "[ChatListContext] TryShow abort: ShowAt failed; jid=" + (chat.JID ?? "?"));
                return false;
            }

            // Same-row right-click never hits SelectionChanged — do not leave ignore sticky
            // or the next left-click would be swallowed.
            _ignoreNextSelectionFromContext = false;
            Debug.WriteLine(
                "[ChatListContext] TryShow OK; jid=" + (chat.JID ?? "?") +
                "; pos=" + position.X.ToString("0") + "," + position.Y.ToString("0"));
            return true;
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

        private static ChatItem ResolveContainerChat(ListViewItem container)
        {
            if (container == null)
            {
                return null;
            }

            if (container.Content is ChatItem asContent)
            {
                return asContent;
            }

            if (container.DataContext is ChatItem fromDc)
            {
                return fromDc;
            }

            if (container.Content is FrameworkElement root && root.DataContext is ChatItem fromRoot)
            {
                return fromRoot;
            }

            return null;
        }

        private void OpenChatActions(ChatItem chat)
        {
            if (chat == null || ChatActionsScrim == null || ChatActionsBarHost == null)
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

            _actionsChat = chat;
            ApplyChatActionsChrome(chat);
            // Same as ChatDetail MessageActions: scrim over list, bar on row 3 above ListCommandBar.
            if (ListCommandBarHost != null)
            {
                ListCommandBarHost.IsHitTestVisible = false;
            }

            if (ChatList != null)
            {
                ChatList.IsHitTestVisible = false;
            }

            if (CommunityHubList != null)
            {
                CommunityHubList.IsHitTestVisible = false;
            }

            ChatActionsScrim.Visibility = Visibility.Visible;
            ChatActionsBarHost.Visibility = Visibility.Visible;
            LegacyCommandBarLayout.CenterPrimaryCommands(ChatActionsBar);
        }

        private void DismissChatActions()
        {
            _actionsChat = null;
            if (ChatActionsScrim != null)
            {
                ChatActionsScrim.Visibility = Visibility.Collapsed;
            }

            if (ChatActionsBarHost != null)
            {
                ChatActionsBarHost.Visibility = Visibility.Collapsed;
            }

            if (ListCommandBarHost != null)
            {
                ListCommandBarHost.IsHitTestVisible = true;
            }

            if (ChatList != null)
            {
                ChatList.IsHitTestVisible = true;
            }

            if (CommunityHubList != null)
            {
                CommunityHubList.IsHitTestVisible = true;
            }
        }

        private void ApplyChatActionsChrome(ChatItem chat)
        {
            if (chat == null)
            {
                return;
            }

            bool pinned = chat.IsChatPinned;
            bool muted = chat.IsMutedLocally;
            bool archived = chat.Status == ChatStatus.Archived;
            bool liveTiles;
            try
            {
                var provider = App.Services?.GetService<ISystemInfoProvider>();
                liveTiles = provider != null
                    ? provider.LiveTilesCompatible()
                    : SystemInfoProvider.DetectLiveTilesCompatible();
            }
            catch
            {
                liveTiles = SystemInfoProvider.DetectLiveTilesCompatible();
            }

            if (ChatActionsPinButton != null)
            {
                ChatActionsPinButton.Label = pinned
                    ? LocalizedStrings.Get("Shell_LegacyChatActions_Unpin.Label", "Unpin")
                    : LocalizedStrings.Get("Shell_LegacyChatActions_Pin.Label", "Pin");
            }

            if (ChatActionsMuteButton != null)
            {
                ChatActionsMuteButton.Label = muted
                    ? LocalizedStrings.Get("Shell_LegacyChatActions_Unmute.Label", "Unmute")
                    : LocalizedStrings.Get("Shell_LegacyChatActions_Mute.Label", "Mute");
            }

            if (ChatActionsArchiveButton != null)
            {
                ChatActionsArchiveButton.Label = archived
                    ? LocalizedStrings.Get("Shell_LegacyChatActions_Unarchive.Label", "Unarchive")
                    : LocalizedStrings.Get("Shell_LegacyChatActions_Archive.Label", "Archive");
            }

            bool canViewProfile = !chat.IsGroup &&
                                  !chat.IsCommunityAnnounce &&
                                  chat.Kind != ChatKind.Community &&
                                  !string.IsNullOrWhiteSpace(chat.JID);
            SetAppBarVisibility(ChatActionsViewProfileButton, canViewProfile);

            bool canAdd = false;
            try
            {
                canAdd = App.Services?.GetService<IContactService>()?.CanAddToAddressBook(chat.JID) == true;
            }
            catch
            {
            }

            SetAppBarVisibility(ChatActionsAddContactButton, canAdd);

            if (ChatActionsFavoriteButton != null)
            {
                ChatActionsFavoriteButton.Label = chat.IsFavorite
                    ? LocalizedStrings.Get("Shell_LegacyChatActions_RemoveFavorite.Label", "Remove from favorites")
                    : LocalizedStrings.Get("Shell_LegacyChatActions_AddFavorite.Label", "Add to favorites");
                SetAppBarVisibility(ChatActionsFavoriteButton, true);
            }

            if (ChatActionsPinToStartButton != null)
            {
                ChatActionsPinToStartButton.Label = chat.IsWidgetPinned
                    ? LocalizedStrings.Get("Shell_LegacyChatActions_UnpinFromStart.Label", "Unpin from Start")
                    : LocalizedStrings.Get("Shell_LegacyChatActions_PinToStart.Label", "Pin to Start");
                SetAppBarVisibility(ChatActionsPinToStartButton, liveTiles);
            }

            SetAppBarVisibility(ChatActionsMute8hButton, !muted);
            SetAppBarVisibility(ChatActionsMute1wButton, !muted);
            SetAppBarVisibility(ChatActionsMuteForeverButton, !muted);
        }

        private static void SetAppBarVisibility(AppBarButton button, bool visible)
        {
            if (button != null)
            {
                button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ChatActionsBar_Loaded(object sender, RoutedEventArgs e)
        {
            if (ChatActionsDeleteButton != null)
            {
                ChatActionsDeleteButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_Delete.Label", "Delete");
            }

            if (ChatActionsViewProfileButton != null)
            {
                ChatActionsViewProfileButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_ViewProfile.Label", "View profile");
            }

            if (ChatActionsAddContactButton != null)
            {
                ChatActionsAddContactButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_AddContact.Label", "Add contact");
            }

            if (ChatActionsMute8hButton != null)
            {
                ChatActionsMute8hButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_Mute8Hours.Label", "8 hours");
            }

            if (ChatActionsMute1wButton != null)
            {
                ChatActionsMute1wButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_Mute1Week.Label", "1 week");
            }

            if (ChatActionsMuteForeverButton != null)
            {
                ChatActionsMuteForeverButton.Label = LocalizedStrings.Get(
                    "Shell_LegacyChatActions_MuteAlways.Label", "Always");
            }

            LegacyCommandBarLayout.CenterPrimaryCommands(sender as CommandBar);
        }

        private void ChatActionsScrim_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            e.Handled = true;
            TryDismissChatActionsFromScrim();
        }

        private void ChatActionsScrim_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            TryDismissChatActionsFromScrim();
        }

        private void TryDismissChatActionsFromScrim()
        {
            // Swallow only the opening finger-up that landed on the new scrim.
            // Do not keep a 500ms grace — that made "click outside" look dead on PC.
            if (!_rowContextGate.ShouldDismissFromScrim(DateTime.UtcNow, ignoreGraceWindow: true))
            {
                return;
            }

            DismissChatActions();
        }

        private void ChatActionsPin_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            if (chat == null)
            {
                return;
            }

            SetChatPinned(chat, !chat.IsChatPinned);
            DismissChatActions();
        }

        private void ChatActionsMute_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            if (chat == null)
            {
                return;
            }

            if (chat.IsMutedLocally)
            {
                SetLocalMute(chat, null);
                DismissChatActions();
                return;
            }

            // Not muted: open the overflow so the user picks a duration (same place as More).
            if (ChatActionsBar != null)
            {
                ChatActionsBar.IsOpen = true;
            }
        }

        private void ChatActionsArchive_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            if (chat == null)
            {
                return;
            }

            if (chat.Status == ChatStatus.Archived)
            {
                UnarchiveChat(chat);
            }
            else
            {
                ArchiveChat(chat);
            }

            DismissChatActions();
        }

        private void ChatActionsDelete_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            if (chat == null)
            {
                return;
            }

            DeleteChat(chat);
            DismissChatActions();
        }

        private void ChatActionsViewProfile_Click(object sender, RoutedEventArgs e)
        {
            ViewProfile(_actionsChat);
            DismissChatActions();
        }

        private void ChatActionsPinToStart_Click(object sender, RoutedEventArgs e)
        {
            PinChatToStart(_actionsChat);
            DismissChatActions();
        }

        private async void ChatActionsFavorite_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            DismissChatActions();
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
                Debug.WriteLine("[ChatListView] Favorite failed: " + ex.Message);
            }
        }

        private async void ChatActionsAddContact_Click(object sender, RoutedEventArgs e)
        {
            var chat = _actionsChat;
            DismissChatActions();
            if (chat == null)
            {
                return;
            }

            IContactService contacts = null;
            try
            {
                contacts = App.Services?.GetService<IContactService>();
            }
            catch
            {
            }

            if (contacts == null)
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
                Debug.WriteLine("[ChatListView] Add contact failed: " + ex.Message);
            }
        }

        private void ChatActionsMute8h_Click(object sender, RoutedEventArgs e) =>
            MuteActionsChatWith(c => c.MuteUntilEightHoursFromNow());

        private void ChatActionsMute1w_Click(object sender, RoutedEventArgs e) =>
            MuteActionsChatWith(c => c.MuteUntilOneWeekFromNow());

        private void ChatActionsMuteForever_Click(object sender, RoutedEventArgs e) =>
            MuteActionsChatWith(c => c.MuteForeverUnixSeconds);

        private void MuteActionsChatWith(Func<IChatService, long> untilFactory)
        {
            var chat = _actionsChat;
            IChatService chats = null;
            try
            {
                chats = App.Services?.GetService<IChatService>();
            }
            catch
            {
            }

            if (chat == null || chats == null || untilFactory == null)
            {
                DismissChatActions();
                return;
            }

            SetLocalMute(chat, untilFactory(chats));
            DismissChatActions();
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

        /// <summary>Context flyout on a row: open the contact Profile page.</summary>
        public void ViewProfile(ChatItem chat)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
            {
                return;
            }

            try
            {
                App.Services?.GetService<ShellViewModel>()?.QueueOpenProfile(chat.JID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListView] ViewProfile failed: " + ex.Message);
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
