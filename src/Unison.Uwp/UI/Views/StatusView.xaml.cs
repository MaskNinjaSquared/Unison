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
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Shell content: Status list + viewer (same pane geometry as ChatsView).</summary>
    public sealed partial class StatusView : Page
    {
        private ShellViewModel _shell;
        private bool _hooked;
        private bool _hasSelectedAuthor;
        private bool _splitterDragging;
        private bool _splitterHover;
        private double _dragStartX;
        private double _dragStartListWidth;
        private CoreCursor _previousCursor;

        public event EventHandler MenuClicked;

        public StatusView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            PaneSplitter.Width = ChatPaneLayoutConstants.SplitterWidth;
            Column0.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            Column0.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
            Column1.MinWidth = ChatPaneLayoutConstants.MinDetailWidth;
            Loaded += StatusView_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _shell = App.Services?.GetService<ShellViewModel>();
            if (_shell != null && !_hooked)
            {
                _shell.PropertyChanged += Shell_PropertyChanged;
                StatusDetailPart.BackRequested += StatusDetailPart_BackRequested;
                StatusListPart.SelectionCleared += StatusListPart_SelectionCleared;
                _hooked = true;
            }

            ApplyPaneState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            if (_shell != null && _hooked)
            {
                _shell.PropertyChanged -= Shell_PropertyChanged;
                StatusDetailPart.BackRequested -= StatusDetailPart_BackRequested;
                StatusListPart.SelectionCleared -= StatusListPart_SelectionCleared;
                _hooked = false;
            }

            _ = StatusDetailPart.ClearAsync();
        }

        private void StatusView_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyPaneState();
        }

        private void Shell_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.IsNarrowWindow))
            {
                ApplyPaneState();
            }
            else if (e.PropertyName == nameof(ShellViewModel.ChatListPaneWidth) && IsWideBoth())
            {
                ApplyListWidth(_shell.ChatListPaneWidth);
                UpdateSplitterPosition();
            }
        }

        private void ApplyPaneState()
        {
            bool narrow = _shell != null && _shell.IsNarrowWindow;
            string state = !narrow
                ? ShellViewModel.PaneWideBoth
                : (_hasSelectedAuthor ? ShellViewModel.PaneNarrowDetail : ShellViewModel.PaneNarrowList);
            VisualStateManager.GoToState(this, state, false);

            bool wideBoth = string.Equals(state, ShellViewModel.PaneWideBoth, StringComparison.Ordinal);
            if (wideBoth)
            {
                ApplyListWidth(_shell != null ? _shell.ChatListPaneWidth : ChatPaneLayoutConstants.DefaultListWidth);
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

            double current = Column0.ActualWidth > 0
                ? Column0.ActualWidth
                : (_shell != null ? _shell.ChatListPaneWidth : ChatPaneLayoutConstants.DefaultListWidth);
            ApplyListWidth(current);
            UpdateSplitterPosition();
        }

        private bool IsWideBoth()
        {
            return _shell != null &&
                   !_shell.IsNarrowWindow &&
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
            ApplyListWidth(_dragStartListWidth + (x - _dragStartX));
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

        private async void StatusDetailPart_BackRequested(object sender, EventArgs e)
        {
            await CloseDetailAsync();
        }

        private async void StatusListPart_SelectionCleared(object sender, EventArgs e)
        {
            await CloseDetailAsync();
        }

        private async void StatusListPart_AuthorSelected(object sender, StatusAuthorSelectedEventArgs e)
        {
            if (e?.Author == null)
            {
                return;
            }

            try
            {
                await StatusDetailPart.OpenAuthorAsync(e.Author);
                _hasSelectedAuthor = StatusDetailPart.HasOpenAuthor;
                ApplyPaneState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusView] Failed to open status: " + ex.Message);
            }
        }

        private async Task CloseDetailAsync()
        {
            StatusListPart.ClearSelection();
            try
            {
                await StatusDetailPart.ClearAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusView] Failed to clear status: " + ex.Message);
            }

            _hasSelectedAuthor = false;
            ApplyPaneState();
        }

        private void StatusListPart_MenuClicked(object sender, EventArgs e)
        {
            if (MenuClicked != null)
            {
                MenuClicked.Invoke(this, EventArgs.Empty);
                return;
            }

            try
            {
                var shell = App.Services?.GetService<ShellViewModel>();
                if (shell != null)
                {
                    shell.IsPaneOpen = !shell.IsPaneOpen;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StatusView] Menu fallback failed: " + ex.Message);
            }
        }

        public bool TryHandleBack()
        {
            if (_hasSelectedAuthor)
            {
                _ = CloseDetailAsync();
                return true;
            }

            return false;
        }
    }
}
