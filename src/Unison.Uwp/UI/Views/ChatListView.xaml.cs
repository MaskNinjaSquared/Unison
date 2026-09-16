using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Views
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
    public sealed partial class ChatListView : UserControl
    {
        public ChatListViewModel ViewModel { get; private set; }

        /// <summary>A conversation is open. Raised for re-binds too: after a PN/LID merge the same
        /// conversation is a different <see cref="ChatItem"/> instance.</summary>
        public event EventHandler<ChatSelectedEventArgs> ChatSelected;

        public event EventHandler MenuClicked;

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

        public ChatListView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<ChatListViewModel>();
                DataContext = ViewModel;
            }

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
            if (scope != ChatListScope.Archived || HeaderTitle == null || FilterButton == null)
            {
                return;
            }

            var strings = App.Services?.GetService<IStringResources>();
            HeaderTitle.Text = strings?.Get("Shell_Archived.Text", "Archived chats") ?? "Archived chats";
            FilterButton.Visibility = Visibility.Collapsed;
        }

        private void ChatListView_Loaded(object sender, RoutedEventArgs e)
        {
            TryAttach();
        }

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

        private void ChatList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args == null || args.InRecycleQueue || args.Item == null)
            {
                return;
            }

            // On demand, and only for rows that actually reached the screen. Fetching every
            // avatar up front cost hundreds of requests for rows nobody scrolled to.
            ViewModel?.OnRowRealized(args.Item as ChatItem);
        }

        /// <summary>Context flyout on a row: pin the conversation as a Start tile.</summary>
        internal void PinChatToStart(ChatItem chat)
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
        internal void SetChatPinned(ChatItem chat, bool pinned)
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
        internal void DeleteChat(ChatItem chat)
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

        internal void ArchiveChat(ChatItem chat)
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

        internal void UnarchiveChat(ChatItem chat)
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
        internal void SetLocalMute(ChatItem chat, long? mutedUntilUnixSeconds)
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

        private async void ViewModel_OpenChatRequested(object sender, string jid)
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

            if (chat == null)
            {
                return;
            }

            // Deliberately not quiet: opening a new chat should behave like picking it.
            ViewModel.EnsureVisible(chat);
            ChatList.SelectedItem = chat;
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

        private IConversationShellPage FindAncestorConversationShellPage()
        {
            DependencyObject current = this;
            while (current != null)
            {
                var match = current as IConversationShellPage;
                if (match != null)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return Window.Current?.Content != null
                ? FindInSubtree<IConversationShellPage>(Window.Current.Content as DependencyObject)
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

        private void HeaderTitle_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != Windows.UI.Input.HoldingState.Started)
            {
                return;
            }

            e.Handled = true;
            _ = OpenRuntimeDiagnosticsAsync();
        }

        private void HeaderTitle_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            e.Handled = true;
            _ = OpenRuntimeDiagnosticsAsync();
        }

        private async Task OpenRuntimeDiagnosticsAsync()
        {
            try
            {
                IDialogService dialogs = App.Services?.GetService<IDialogService>();
                if (dialogs == null)
                {
                    return;
                }

                await dialogs.ShowRuntimeDiagnosticsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatListView] Runtime diagnostics dialog failed: " + ex.Message);
            }
        }
    }
}
