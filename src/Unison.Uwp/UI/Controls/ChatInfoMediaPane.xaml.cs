using System;
using System.Collections;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatInfoMediaPane : UserControl
    {
        private bool _squareSyncQueued;
        private double _lastItemHeight;

        public ChatInfoMediaPane()
        {
            InitializeComponent();
            MediaGrid.ContainerContentChanging += MediaGrid_ContainerContentChanging;
        }

        public void Bind(IEnumerable items, bool hasItems, string emptyText)
        {
            if (MediaEmptyText != null)
            {
                MediaEmptyText.Text = emptyText ?? string.Empty;
            }

            if (MediaEmptyHost != null)
            {
                MediaEmptyHost.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            }

            if (MediaGrid != null)
            {
                if (!ReferenceEquals(MediaGrid.ItemsSource, items))
                {
                    MediaGrid.ItemsSource = items;
                }

                MediaGrid.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
                if (hasItems)
                {
                    QueueSyncSquareItemSize();
                }
            }
        }

        public FrameworkElement Host => MediaHost;

        private void MediaGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            ChatDetailInfoPivotHelper.HandleMediaItemClick(this, e.ClickedItem);
        }

        private void MediaGrid_Loaded(object sender, RoutedEventArgs e)
        {
            QueueSyncSquareItemSize();
        }

        private void MediaGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.PreviousSize.Width == e.NewSize.Width && MediaGrid.ItemHeight > 0)
            {
                return;
            }

            QueueSyncSquareItemSize();
        }

        private void MediaGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.ItemIndex != 0)
            {
                return;
            }

            QueueSyncSquareItemSize();
        }

        private void QueueSyncSquareItemSize()
        {
            if (_squareSyncQueued || MediaGrid == null || MediaGrid.Visibility != Visibility.Visible)
            {
                return;
            }

            _squareSyncQueued = true;
            var ignore = Dispatcher.RunAsync(CoreDispatcherPriority.Low, SyncSquareItemSize);
        }

        /// <summary>
        /// AdaptiveGridView stretches item width to fill columns but keeps ItemHeight fixed.
        /// Copy the resolved item width onto ItemHeight so every tile stays square.
        /// Deferred off the layout pass so this cannot raise LayoutCycleException.
        /// </summary>
        private void SyncSquareItemSize()
        {
            _squareSyncQueued = false;
            if (MediaGrid == null || MediaGrid.ActualWidth <= 0 || MediaGrid.Visibility != Visibility.Visible)
            {
                return;
            }

            var first = MediaGrid.ContainerFromIndex(0) as FrameworkElement;
            double itemWidth = 0;
            if (first != null)
            {
                if (!double.IsNaN(first.Width) && first.Width > 0)
                {
                    itemWidth = first.Width;
                }
                else if (first.ActualWidth > 0)
                {
                    itemWidth = first.ActualWidth;
                }
            }

            if (itemWidth <= 0)
            {
                itemWidth = CalculateExpectedItemWidth();
            }

            if (itemWidth <= 0)
            {
                return;
            }

            if (Math.Abs(MediaGrid.ItemHeight - itemWidth) <= 1 && Math.Abs(_lastItemHeight - itemWidth) <= 1)
            {
                return;
            }

            _lastItemHeight = itemWidth;
            MediaGrid.ItemHeight = itemWidth;
        }

        private double CalculateExpectedItemWidth()
        {
            double containerWidth = MediaGrid.ActualWidth;
            var itemsPanel = MediaGrid.ItemsPanelRoot;
            if (itemsPanel != null)
            {
                containerWidth -= itemsPanel.Margin.Left + itemsPanel.Margin.Right;
            }

            containerWidth -= MediaGrid.Padding.Left + MediaGrid.Padding.Right;
            containerWidth -= MediaGrid.BorderThickness.Left + MediaGrid.BorderThickness.Right;
            if (containerWidth <= 0 || double.IsNaN(MediaGrid.DesiredWidth) || MediaGrid.DesiredWidth <= 0)
            {
                return 0;
            }

            int columns = (int)Math.Round(containerWidth / MediaGrid.DesiredWidth);
            if (columns < 1)
            {
                columns = 1;
            }

            var first = MediaGrid.ContainerFromIndex(0) as FrameworkElement;
            double itemMargin = first != null
                ? first.Margin.Left + first.Margin.Right
                : 2;

            return Math.Floor((containerWidth / columns) - itemMargin);
        }
    }
}
