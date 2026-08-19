using System.Collections;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatInfoFilesPane : UserControl
    {
        private ChatDetailInfoViewModel _pagingViewModel;
        private ScrollViewer _scrollViewer;

        public ChatInfoFilesPane()
        {
            InitializeComponent();
        }

        /// <summary>Rows are materialized a page at a time; scrolling to the bottom asks for more.</summary>
        public void AttachPaging(ChatDetailInfoViewModel viewModel)
        {
            _pagingViewModel = viewModel;
            EnsureScrollHook();
        }

        public void Bind(IEnumerable items, bool hasItems, string emptyText, bool isLoading)
        {
            if (FilesEmptyText != null)
            {
                FilesEmptyText.Text = emptyText ?? string.Empty;
            }

            if (FilesLoadingRing != null)
            {
                FilesLoadingRing.IsActive = isLoading;
                FilesLoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FilesEmptyHost != null)
            {
                FilesEmptyHost.Visibility = (!isLoading && !hasItems) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FilesList != null)
            {
                if (isLoading)
                {
                    FilesList.Visibility = Visibility.Collapsed;
                }
                else
                {
                    if (!ReferenceEquals(FilesList.ItemsSource, items))
                    {
                        FilesList.ItemsSource = items;
                    }

                    FilesList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
                    if (hasItems)
                    {
                        EnsureScrollHook();
                    }
                }
            }
        }

        public FrameworkElement Host => FilesHost;

        private void EnsureScrollHook()
        {
            if (_scrollViewer != null || FilesList == null)
            {
                return;
            }

            _scrollViewer = FindScrollViewer(FilesList);
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= FilesScroll_ViewChanged;
                _scrollViewer.ViewChanged += FilesScroll_ViewChanged;
            }
        }

        private void FilesScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var vm = _pagingViewModel;
            var scroll = _scrollViewer;
            if (vm == null || scroll == null || !vm.CanLoadMoreFiles || scroll.ScrollableHeight <= 0)
            {
                return;
            }

            if (scroll.VerticalOffset < scroll.ScrollableHeight - 240)
            {
                return;
            }

            vm.LoadMoreFiles();
        }

        private static ScrollViewer FindScrollViewer(DependencyObject element)
        {
            var scroll = element as ScrollViewer;
            if (scroll != null)
            {
                return scroll;
            }

            int count = VisualTreeHelper.GetChildrenCount(element);
            for (int i = 0; i < count; i++)
            {
                ScrollViewer found = FindScrollViewer(VisualTreeHelper.GetChild(element, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
