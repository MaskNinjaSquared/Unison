using System;
using Unison.Core.Constants;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Two-pane resize grip with the same behavior as the chats list/detail splitter:
    /// chrome hidden until hover or drag, drag resizes the left column within
    /// <see cref="ChatPaneLayoutConstants"/> limits.
    /// </summary>
    internal sealed class PaneSplitterHost
    {
        private readonly Border _splitter;
        private readonly UIElement _chrome;
        private readonly FrameworkElement _root;
        private readonly ColumnDefinition _leftColumn;
        private readonly ColumnDefinition _rightColumn;

        private bool _wantTwoPane;
        private bool _isEnabled;
        private bool _hover;
        private bool _dragging;
        private double _dragStartX;
        private double _dragStartWidth;
        private double _leftWidth = ChatPaneLayoutConstants.DefaultListWidth;
        private CoreCursor _previousCursor;

        public PaneSplitterHost(
            Border splitter,
            UIElement chrome,
            FrameworkElement root,
            ColumnDefinition leftColumn,
            ColumnDefinition rightColumn)
        {
            _splitter = splitter ?? throw new ArgumentNullException(nameof(splitter));
            _chrome = chrome ?? throw new ArgumentNullException(nameof(chrome));
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _leftColumn = leftColumn ?? throw new ArgumentNullException(nameof(leftColumn));
            _rightColumn = rightColumn ?? throw new ArgumentNullException(nameof(rightColumn));

            _splitter.PointerEntered += OnPointerEntered;
            _splitter.PointerExited += OnPointerExited;
            _splitter.PointerPressed += OnPointerPressed;
            _splitter.PointerMoved += OnPointerMoved;
            _splitter.PointerReleased += OnPointerReleased;
            _splitter.PointerCaptureLost += OnPointerCaptureLost;
            _root.SizeChanged += OnRootSizeChanged;
        }

        /// <summary>Shows both panes with the grip, or collapses to the left pane only.</summary>
        public void SetTwoPane(bool isTwoPane)
        {
            _wantTwoPane = isTwoPane;
            ApplyTwoPaneLayout();
        }

        private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_wantTwoPane)
            {
                return;
            }

            ApplyTwoPaneLayout();
        }

        /// <summary>
        /// Same floor as chat list/detail: both columns need
        /// <see cref="ChatPaneLayoutConstants.MinListWidth"/> +
        /// <see cref="ChatPaneLayoutConstants.MinDetailWidth"/> or the grip collapses.
        /// </summary>
        private bool CanFitTwoPane()
        {
            double total = _root.ActualWidth;
            return total >=
                   ChatPaneLayoutConstants.MinListWidth + ChatPaneLayoutConstants.MinDetailWidth;
        }

        private void ApplyTwoPaneLayout()
        {
            bool show = _wantTwoPane && CanFitTwoPane();
            if (_isEnabled == show && show)
            {
                ApplyLeftWidth(_leftWidth);
                UpdatePosition();
                return;
            }

            _isEnabled = show;
            _splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (show)
            {
                _rightColumn.MinWidth = ChatPaneLayoutConstants.MinDetailWidth;
                _rightColumn.Width = new GridLength(1, GridUnitType.Star);
                ApplyLeftWidth(_leftWidth);
            }
            else
            {
                if (_dragging)
                {
                    EndDrag(null);
                }

                _hover = false;
                _leftColumn.Width = new GridLength(1, GridUnitType.Star);
                _leftColumn.MinWidth = 0;
                _leftColumn.MaxWidth = double.PositiveInfinity;
                _rightColumn.MinWidth = 0;
                _rightColumn.Width = new GridLength(0);
                UpdateChrome();
            }

            UpdatePosition();
        }

        private void ApplyLeftWidth(double desired)
        {
            double max = GetMaxLeftWidth();
            double width = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(max, desired));
            _leftColumn.Width = new GridLength(width);
            _leftColumn.MinWidth = ChatPaneLayoutConstants.MinListWidth;
            _leftColumn.MaxWidth = ChatPaneLayoutConstants.MaxListWidth;
        }

        private double GetMaxLeftWidth()
        {
            double total = _root.ActualWidth;
            if (total <= 0)
            {
                return ChatPaneLayoutConstants.MaxListWidth;
            }

            // Keep at least MinDetailWidth on the right; never invent room that is not there.
            double maxFromDetail = total - ChatPaneLayoutConstants.MinDetailWidth;
            if (maxFromDetail < ChatPaneLayoutConstants.MinListWidth)
            {
                return ChatPaneLayoutConstants.MinListWidth;
            }

            return Math.Min(ChatPaneLayoutConstants.MaxListWidth, maxFromDetail);
        }

        private double CurrentLeftWidth()
        {
            if (_leftColumn.ActualWidth > 0)
            {
                return _leftColumn.ActualWidth;
            }

            return _leftColumn.Width.IsAbsolute ? _leftColumn.Width.Value : _leftWidth;
        }

        private void UpdatePosition()
        {
            if (!_isEnabled)
            {
                return;
            }

            double left = Math.Max(0, CurrentLeftWidth() - ChatPaneLayoutConstants.SplitterOverlapList);
            _splitter.Margin = new Thickness(left, 0, 0, 0);
        }

        private void UpdateChrome()
        {
            _chrome.Opacity = (_hover || _dragging) ? 1 : 0;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (!_isEnabled)
            {
                return;
            }

            _hover = true;
            UpdateChrome();
            SetResizeCursor();
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                return;
            }

            _hover = false;
            UpdateChrome();
            RestoreCursor();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isEnabled)
            {
                return;
            }

            _dragging = true;
            _hover = true;
            _dragStartX = e.GetCurrentPoint(_root).Position.X;
            _dragStartWidth = CurrentLeftWidth();
            _splitter.CapturePointer(e.Pointer);
            UpdateChrome();
            SetResizeCursor();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            double delta = e.GetCurrentPoint(_root).Position.X - _dragStartX;
            ApplyLeftWidth(_dragStartWidth + delta);
            UpdatePosition();
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            EndDrag(e.Pointer);
            e.Handled = true;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                EndDrag(null);
            }
        }

        private void EndDrag(Pointer pointer)
        {
            _dragging = false;
            if (pointer != null)
            {
                _splitter.ReleasePointerCapture(pointer);
            }

            _leftWidth = Math.Max(
                ChatPaneLayoutConstants.MinListWidth,
                Math.Min(GetMaxLeftWidth(), CurrentLeftWidth()));

            UpdateChrome();
            if (!_hover)
            {
                RestoreCursor();
            }
        }

        private void SetResizeCursor()
        {
            CoreWindow window = Window.Current?.CoreWindow;
            if (window == null)
            {
                return;
            }

            if (_previousCursor == null)
            {
                _previousCursor = window.PointerCursor;
            }

            window.PointerCursor = new CoreCursor(CoreCursorType.SizeWestEast, 1);
        }

        private void RestoreCursor()
        {
            CoreWindow window = Window.Current?.CoreWindow;
            if (window == null)
            {
                return;
            }

            window.PointerCursor = _previousCursor ?? new CoreCursor(CoreCursorType.Arrow, 1);
            _previousCursor = null;
        }
    }
}
