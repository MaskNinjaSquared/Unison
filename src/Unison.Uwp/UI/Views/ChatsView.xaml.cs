using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell content: chat list + detail (master-detail VisualStates).</summary>
    public sealed partial class ChatsView : Page
    {
        private ShellViewModel _shell;
        private bool _hooked;
        private bool _splitterDragging;
        private bool _splitterHover;
        private bool _openingDeepLink;
        private double _dragStartX;
        private double _dragStartListWidth;
        private CoreCursor _previousCursor;

        public event EventHandler MenuClicked;

        public ChatsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            PaneSplitter.Width = ChatPaneLayoutConstants.SplitterWidth;
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
            Column1.MinWidth = ChatPaneLayoutConstants.MinDetailWidth;
            Loaded += ChatsView_Loaded;
        }

        public bool HasActiveChat => ChatDetailPart?.HasActiveChat == true;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _shell = App.Services?.GetService<ShellViewModel>();
            if (_shell != null && !_hooked)
            {
                _shell.PropertyChanged += Shell_PropertyChanged;
                ChatDetailPart.BackRequested += ChatDetailPart_BackRequested;
                _hooked = true;
            }

            ApplyChatPaneState();
            _ = TryOpenPendingDeepLinkAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_shell != null && _hooked)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                ChatDetailPart.BackRequested -= ChatDetailPart_BackRequested;
                _hooked = false;
            }
        }

        /// <summary>Logout / session wipe — clear detail + selection before shell is torn down.</summary>
        public async Task ResetForLoggedOutAsync()
        {
            try
            {
                NavigationCacheMode = NavigationCacheMode.Disabled;
                ChatListPart?.ClearSelection();
                if (ChatDetailPart != null)
                {
                    await ChatDetailPart.SetActiveChatAsync(null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] ResetForLoggedOutAsync: " + ex.Message);
            }

            _shell?.ClearChat();
        }

        /// <summary>Called when local chats are wiped (resync) — leave NarrowDetail empty state.</summary>
        internal async void NotifyLocalConversationsCleared()
        {
            try
            {
                if (ChatDetailPart != null)
                {
                    await ChatDetailPart.SetActiveChatAsync(null);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] NotifyLocalConversationsCleared: " + ex.Message);
            }

            _shell?.ClearChat();
        }

        private void ChatsView_Loaded(object sender, RoutedEventArgs e)
        {
            _ = TryOpenPendingDeepLinkAsync();
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ChatPane) ||
                e.PropertyName == nameof(ShellViewModel.HasActiveChat) ||
                e.PropertyName == nameof(ShellViewModel.IsNarrowWindow))
            {
                ReconcileMinimalEmptyDetail();
                if (e.PropertyName == nameof(ShellViewModel.ChatPane) ||
                    e.PropertyName == nameof(ShellViewModel.IsNarrowWindow))
                {
                    ApplyChatPaneState();
                }
            }
            else if (e.PropertyName == nameof(ShellViewModel.ChatListPaneWidth) && IsWideBoth())
            {
                ApplyListWidth(_shell.ChatListPaneWidth);
                UpdateSplitterPosition();
            }
            else if (e.PropertyName == nameof(ShellViewModel.PendingOpenChatJid))
            {
                _ = TryOpenPendingDeepLinkAsync();
            }
        }

        /// <summary>
        /// Minimal: if chat space is visible with no open intent / no chat, close it and show the list.
        /// Do not tear down while <see cref="ShellViewModel.PendingChat"/> is set — that means we
        /// deliberately opened NarrowDetail (UI HasActiveChat can lag one frame behind SelectChat).
        /// </summary>
        private void ReconcileMinimalEmptyDetail()
        {
            if (_shell == null || !_shell.IsNarrowWindow)
            {
                return;
            }

            if (!string.Equals(_shell.ChatPane, ShellViewModel.PaneNarrowDetail, StringComparison.Ordinal))
            {
                return;
            }

            // Opening or open: shell still owns a chat — leave NarrowDetail alone.
            if (_shell.PendingChat != null && _shell.HasActiveChat)
            {
                return;
            }

            bool detailEmpty = ChatDetailPart == null || !ChatDetailPart.HasActiveChat;
            if (!detailEmpty && _shell.HasActiveChat)
            {
                return;
            }

            try
            {
                ChatListPart?.ClearSelection();
            }
            catch
            {
            }

            _shell.ClearChat();
        }

        /// <summary>
        /// Opens a chat queued from secondary tile / toast (<c>chat=</c>), including NarrowDetail.
        /// </summary>
        public void RequestOpenPendingDeepLink()
        {
            _ = TryOpenPendingDeepLinkAsync();
        }

        private async Task TryOpenPendingDeepLinkAsync()
        {
            if (_shell == null || _openingDeepLink)
            {
                return;
            }

            string jid = _shell.PendingOpenChatJid;
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            _openingDeepLink = true;
            try
            {
                ChatItem chat = null;
                for (int i = 0; i < 50; i++)
                {
                    jid = _shell.PendingOpenChatJid;
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        return;
                    }

                    chat = ChatListPart?.FindChatByJid(jid);
                    if (chat != null)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }

                if (chat == null)
                {
                    Debug.WriteLine("[ChatsView] Deep-link chat not found yet: " + jid);
                    return;
                }

                _shell.ClearPendingOpenChatJid();
                ChatListPart?.HighlightChatQuiet(chat);

                await ChatDetailPart.SetActiveChatAsync(chat);
                if (ChatDetailPart.HasActiveChat)
                {
                    _shell.SelectChat(chat);
                    ApplyChatPaneState();
                    _shell.ReportActiveChat(true);
                }
                else
                {
                    _shell.ClearChat();
                    ApplyChatPaneState();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] Deep-link open failed: " + ex);
            }
            finally
            {
                _openingDeepLink = false;
            }
        }

        private void ApplyChatPaneState()
        {
            if (_shell == null)
            {
                return;
            }

            ReconcileMinimalEmptyDetail();

            string pane = _shell.ChatPane;
            VisualStateManager.GoToState(this, pane, false);

            bool wideBoth = string.Equals(pane, ShellViewModel.PaneWideBoth, StringComparison.Ordinal);
            if (wideBoth)
            {
                ApplyListWidth(_shell.ChatListPaneWidth);
                UpdateSplitterPosition();
                UpdateSplitterChrome();
            }
            else
            {
                _splitterDragging = false;
                _splitterHover = false;
                SplitterChrome.Opacity = 0;
                RestoreCursor();
            }
        }

        private void RootContentGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            // Keep list within min/max when the window shrinks.
            double current = Column0.ActualWidth > 0 ? Column0.ActualWidth : _shell.ChatListPaneWidth;
            ApplyListWidth(current);
            UpdateSplitterPosition();
        }

        private bool IsWideBoth()
        {
            return _shell != null &&
                   string.Equals(_shell.ChatPane, ShellViewModel.PaneWideBoth, StringComparison.Ordinal) &&
                   PaneSplitter.Visibility == Visibility.Visible;
        }

        private void ApplyListWidth(double desired)
        {
            double max = GetMaxListWidth();
            double width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(max, desired));
            Column0.Width = new GridLength(width);
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
        }

        private double GetMaxListWidth()
        {
            double total = RootContentGrid.ActualWidth;
            if (total <= 0)
            {
                return ChatPaneLayoutConstants.MaxListWidth;
            }

            double maxFromDetail = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                total - ChatPaneLayoutConstants.MinDetailWidth);
            return Math.Min(ChatPaneLayoutConstants.MaxListWidth, maxFromDetail);
        }

        private void UpdateSplitterPosition()
        {
            if (PaneSplitter == null || !IsWideBoth())
            {
                return;
            }

            double listWidth = Column0.ActualWidth;
            if (listWidth <= 0 && Column0.Width.IsAbsolute)
            {
                listWidth = Column0.Width.Value;
            }

            double left = Math.Max(0, listWidth - ChatPaneLayoutConstants.SplitterOverlapList);
            PaneSplitter.Margin = new Thickness(left, 0, 0, 0);
        }

        private void UpdateSplitterChrome()
        {
            if (SplitterChrome == null)
            {
                return;
            }

            SplitterChrome.Opacity = (_splitterHover || _splitterDragging) ? 1 : 0;
        }

        private void PaneSplitter_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            _splitterHover = true;
            UpdateSplitterChrome();
            SetResizeCursor();
        }

        private void PaneSplitter_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                return;
            }

            _splitterHover = false;
            UpdateSplitterChrome();
            RestoreCursor();
        }

        private void PaneSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!IsWideBoth())
            {
                return;
            }

            _splitterDragging = true;
            _splitterHover = true;
            _dragStartX = e.GetCurrentPoint(RootContentGrid).Position.X;
            _dragStartListWidth = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (Column0.Width.IsAbsolute
                    ? Column0.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);

            PaneSplitter.CapturePointer(e.Pointer);
            UpdateSplitterChrome();
            SetResizeCursor();
            e.Handled = true;
        }

        private void PaneSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            double x = e.GetCurrentPoint(RootContentGrid).Position.X;
            double delta = x - _dragStartX;
            ApplyListWidth(_dragStartListWidth + delta);
            UpdateSplitterPosition();
            e.Handled = true;
        }

        private void PaneSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_splitterDragging)
            {
                return;
            }

            EndSplitterDrag(e.Pointer);
            e.Handled = true;
        }

        private void PaneSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_splitterDragging)
            {
                EndSplitterDrag(null);
            }
        }

        private void EndSplitterDrag(Pointer pointer)
        {
            _splitterDragging = false;
            if (pointer != null)
            {
                try
                {
                    PaneSplitter.ReleasePointerCapture(pointer);
                }
                catch
                {
                }
            }

            double width = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (Column0.Width.IsAbsolute
                    ? Column0.Width.Value
                    : ChatPaneLayoutConstants.DefaultListWidth);
            width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(GetMaxListWidth(), width));

            if (_shell != null)
            {
                _shell.ChatListPaneWidth = width;
            }

            UpdateSplitterChrome();
            if (!_splitterHover)
            {
                RestoreCursor();
            }
        }

        private void SetResizeCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                if (_previousCursor == null)
                {
                    _previousCursor = window.CoreWindow.PointerCursor;
                }

                window.CoreWindow.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 1);
            }
            catch
            {
            }
        }

        private void RestoreCursor()
        {
            try
            {
                var window = Window.Current;
                if (window?.CoreWindow == null)
                {
                    return;
                }

                window.CoreWindow.PointerCursor = _previousCursor ?? new CoreCursor(CoreCursorType.Arrow, 1);
                _previousCursor = null;
            }
            catch
            {
            }
        }

        private async void ChatDetailPart_BackRequested(object sender, EventArgs e)
        {
            ChatListPart.ClearSelection();
            try
            {
                await ChatDetailPart.SetActiveChatAsync(null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] Failed to clear chat: " + ex);
            }

            _shell?.ClearChat();
        }

        private async void ChatListPart_ChatSelected(object sender, ChatSelectedEventArgs e)
        {
            // List Moves / dedupe must never tear the open chat down via a null selection.
            if (e?.SelectedChat == null)
            {
                TryRecoverListSelectionFromActiveChat();
                return;
            }

            try
            {
                ChatItem selected = e.SelectedChat;
                if (IsSameActiveConversation(selected) && ChatDetailPart.HasActiveChat)
                {
                    // Keep shell PendingChat on the live list instance; detail rebinds without reload.
                    await ChatDetailPart.SetActiveChatAsync(selected);
                    _shell?.SelectChat(selected);
                    _shell?.ReportActiveChat(true);
                    return;
                }

                await ChatDetailPart.SetActiveChatAsync(selected);
                bool opened = ChatDetailPart.HasActiveChat && IsSameActiveConversation(selected);
                if (opened)
                {
                    _shell?.SelectChat(selected);
                    _shell?.ReportActiveChat(true);
                    ApplyChatPaneState();
                }
                else if (ChatDetailPart.HasActiveChat)
                {
                    // Open race/cancel while another (or same) chat remains visible — keep it.
                    TryRecoverListSelectionFromActiveChat();
                }
                else
                {
                    // Genuine failed open — leave empty, but try restoring list highlight by jid.
                    Debug.WriteLine("[ChatsView] Open chat did not activate UI for " + selected.JID);
                    ChatListPart.HighlightChatQuiet(selected);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] Failed to open chat: " + ex);
                if (ChatDetailPart?.HasActiveChat == true)
                {
                    TryRecoverListSelectionFromActiveChat();
                    return;
                }

                try
                {
                    ChatListPart.ClearSelection();
                    await ChatDetailPart.SetActiveChatAsync(null);
                }
                catch
                {
                }

                _shell?.ClearChat();
                ApplyChatPaneState();
            }
        }

        private bool IsSameActiveConversation(ChatItem chat)
        {
            if (chat == null)
            {
                return false;
            }

            ChatItem active = ChatDetailPart?.ActiveChatItem ?? _shell?.PendingChat;
            if (active == null || string.IsNullOrWhiteSpace(active.JID) || string.IsNullOrWhiteSpace(chat.JID))
            {
                return false;
            }

            try
            {
                var service = App.Services?.GetService<IWhatsAppService>();
                if (service != null)
                {
                    return string.Equals(
                        service.GetCanonicalJid(active.JID),
                        service.GetCanonicalJid(chat.JID),
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }

            return string.Equals(active.JID, chat.JID, StringComparison.OrdinalIgnoreCase);
        }

        private void TryRecoverListSelectionFromActiveChat()
        {
            ChatItem active = ChatDetailPart?.ActiveChatItem ?? _shell?.PendingChat;
            if (active == null || string.IsNullOrWhiteSpace(active.JID))
            {
                return;
            }

            try
            {
                ChatItem live = ChatListPart?.FindChatByJid(active.JID) ?? active;
                ChatListPart?.HighlightChatQuiet(live);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] Recover selection failed: " + ex.Message);
            }
        }

        private void ChatListPart_MenuClicked(object sender, EventArgs e)
        {
            int handlers = MenuClicked?.GetInvocationList()?.Length ?? 0;
            Debug.WriteLine("[ChatsView] ChatListPart_MenuClicked → shell handlers=" + handlers);
            if (MenuClicked != null)
            {
                MenuClicked.Invoke(this, EventArgs.Empty);
                return;
            }

            // Fallback if MainView missed WireChatsViewMenu after Settings→Chats.
            TryToggleShellPaneFallback();
        }

        private void TryToggleShellPaneFallback()
        {
            try
            {
                var shell = App.Services?.GetService<ShellViewModel>();
                if (shell == null)
                {
                    return;
                }

                Debug.WriteLine("[ChatsView] Menu fallback toggle via ShellViewModel");
                shell.IsPaneOpen = !shell.IsPaneOpen;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatsView] Menu fallback failed: " + ex.Message);
            }
        }

        public bool TryHandleBack()
        {
            if (ChatDetailPart != null && ChatDetailPart.TryConsumeBack())
            {
                return true;
            }

            if (_shell != null &&
                ((_shell.IsNarrowWindow && _shell.ChatPane == ShellViewModel.PaneNarrowDetail) ||
                 (!_shell.IsNarrowWindow && _shell.HasActiveChat)))
            {
                ChatDetailPart_BackRequested(this, EventArgs.Empty);
                return true;
            }

            return false;
        }
    }
}
