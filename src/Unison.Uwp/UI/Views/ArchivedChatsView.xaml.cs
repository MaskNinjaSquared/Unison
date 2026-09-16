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
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell content: archived chat list + detail (master-detail VisualStates).</summary>
    public sealed partial class ArchivedChatsView : Page, IConversationShellPage
    {
        private ShellViewModel _shell;
        private bool _hooked;
        private bool _splitterDragging;
        private bool _splitterHover;
        private double _dragStartX;
        private double _dragStartListWidth;
        private CoreCursor _previousCursor;

        public event EventHandler MenuClicked;

        public ArchivedChatsView()
        {
            InitializeComponent();
            ChatListPart.ConfigureScope(ChatListScope.Archived);
            NavigationCacheMode = NavigationCacheMode.Disabled;
            PaneSplitter.Width = ChatPaneLayoutConstants.SplitterWidth;
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
            Column1.MinWidth = ChatPaneLayoutConstants.MinDetailWidth;
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

        /// <summary>Logout / session wipe â€” clear detail + selection before shell is torn down.</summary>
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
                Debug.WriteLine("[ArchivedChatsView] ResetForLoggedOutAsync: " + ex.Message);
            }

            _shell?.ClearChat();
        }

        /// <summary>Called when local chats are wiped (resync) â€” leave NarrowDetail empty state.</summary>
        public void NotifyLocalConversationsCleared()
        {
            _ = NotifyLocalConversationsClearedAsync();
        }

        private async Task NotifyLocalConversationsClearedAsync()
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
                Debug.WriteLine("[ArchivedChatsView] NotifyLocalConversationsCleared: " + ex.Message);
            }

            _shell?.ClearChat();
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
        }

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
                Debug.WriteLine("[ArchivedChatsView] Failed to clear chat: " + ex);
            }

            _shell?.ClearChat();
        }

        private async void ChatListPart_ChatSelected(object sender, ChatSelectedEventArgs e)
        {
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
                    await ChatDetailPart.PrepareActiveChatAsync(selected);
                    _shell?.SelectChat(selected);
                    _shell?.ReportActiveChat(true);
                    return;
                }

                await ChatDetailPart.PrepareActiveChatAsync(selected);
                bool opened = ChatDetailPart.HasActiveChat && IsSameActiveConversation(selected);
                if (opened)
                {
                    _shell?.SelectChat(selected);
                    _shell?.ReportActiveChat(true);
                    ApplyChatPaneState();
                    await ChatDetailPart.CompleteActiveChatLoadAsync();
                }
                else if (ChatDetailPart.HasActiveChat)
                {
                    TryRecoverListSelectionFromActiveChat();
                }
                else
                {
                    Debug.WriteLine("[ArchivedChatsView] Open chat did not activate UI for " + selected.JID);
                    ChatListPart.HighlightChatQuiet(selected);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ArchivedChatsView] Failed to open chat: " + ex);
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
                var jids = App.Services?.GetService<IJidResolver>();
                if (jids != null)
                {
                    return string.Equals(
                        jids.GetCanonicalJid(active.JID),
                        jids.GetCanonicalJid(chat.JID),
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
                Debug.WriteLine("[ArchivedChatsView] Recover selection failed: " + ex.Message);
            }
        }

        private void ChatListPart_MenuClicked(object sender, EventArgs e)
        {
            int handlers = MenuClicked?.GetInvocationList()?.Length ?? 0;
            Debug.WriteLine("[ArchivedChatsView] ChatListPart_MenuClicked â†’ shell handlers=" + handlers);
            if (MenuClicked != null)
            {
                MenuClicked.Invoke(this, EventArgs.Empty);
                return;
            }

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

                Debug.WriteLine("[ArchivedChatsView] Menu fallback toggle via ShellViewModel");
                shell.IsPaneOpen = !shell.IsPaneOpen;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ArchivedChatsView] Menu fallback failed: " + ex.Message);
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

        public void RequestOpenPendingDeepLink()
        {
        }
    }
}
