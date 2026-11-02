using System.Collections;
using System.Windows.Input;
using Unison.Core.Models;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Search + SemanticZoom emoji catalog. <see cref="GlyphCommand"/> is insert or react.
    /// </summary>
    public sealed partial class EmojiPickerPanel : UserControl
    {
        public static readonly DependencyProperty CategoriesSourceProperty =
            DependencyProperty.Register(
                nameof(CategoriesSource),
                typeof(object),
                typeof(EmojiPickerPanel),
                new PropertyMetadata(null, OnCategoriesSourceChanged));

        public static readonly DependencyProperty GlyphCommandProperty =
            DependencyProperty.Register(
                nameof(GlyphCommand),
                typeof(ICommand),
                typeof(EmojiPickerPanel),
                new PropertyMetadata(null));

        public static readonly DependencyProperty SearchQueryProperty =
            DependencyProperty.Register(
                nameof(SearchQuery),
                typeof(string),
                typeof(EmojiPickerPanel),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsCatalogLoadingProperty =
            DependencyProperty.Register(
                nameof(IsCatalogLoading),
                typeof(bool),
                typeof(EmojiPickerPanel),
                new PropertyMetadata(false));

        private EmojiCategory _pendingJumpCategory;
        private CollectionViewSource _groupedSource;

        public EmojiPickerPanel()
        {
            InitializeComponent();
            Loaded += (_, __) => BindEmojiLists();
        }

        public object CategoriesSource
        {
            get { return GetValue(CategoriesSourceProperty); }
            set { SetValue(CategoriesSourceProperty, value); }
        }

        public ICommand GlyphCommand
        {
            get { return (ICommand)GetValue(GlyphCommandProperty); }
            set { SetValue(GlyphCommandProperty, value); }
        }

        public string SearchQuery
        {
            get { return (string)GetValue(SearchQueryProperty); }
            set { SetValue(SearchQueryProperty, value); }
        }

        public bool IsCatalogLoading
        {
            get { return (bool)GetValue(IsCatalogLoadingProperty); }
            set { SetValue(IsCatalogLoadingProperty, value); }
        }

        /// <summary>
        /// True when zoomed-out jump view consumed Back (returns to category list).
        /// </summary>
        public bool TryConsumeBack()
        {
            if (EmojiZoom == null || EmojiZoom.IsZoomedInViewActive)
            {
                return false;
            }

            EmojiZoom.IsZoomedInViewActive = true;
            return true;
        }

        public void ResetZoomedIn()
        {
            if (EmojiZoom != null)
            {
                EmojiZoom.IsZoomedInViewActive = true;
            }

            BindEmojiLists();
        }

        private static void OnCategoriesSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var panel = d as EmojiPickerPanel;
            panel?.BindEmojiLists();
        }

        private void BindEmojiLists()
        {
            object source = CategoriesSource;
            if (source == null)
            {
                return;
            }

            if (EmojiCategoryList != null &&
                (_groupedSource == null || !ReferenceEquals(_groupedSource.Source, source)))
            {
                _groupedSource = new CollectionViewSource
                {
                    IsSourceGrouped = true,
                    ItemsPath = new PropertyPath(nameof(EmojiCategory.Glyphs)),
                    Source = source
                };
                EmojiCategoryList.ItemsSource = _groupedSource.View;
            }

            if (EmojiJumpList != null &&
                !ReferenceEquals(EmojiJumpList.ItemsSource, source))
            {
                EmojiJumpList.ItemsSource = source as IEnumerable ?? source;
            }
        }

        private void CategoryHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (EmojiZoom != null)
            {
                EmojiZoom.IsZoomedInViewActive = false;
            }
        }

        private void EmojiGlyph_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var host = sender as FrameworkElement;
            string glyph = host?.DataContext as string;
            if (string.IsNullOrEmpty(glyph))
            {
                return;
            }

            ICommand command = GlyphCommand;
            if (command != null && command.CanExecute(glyph))
            {
                command.Execute(glyph);
            }

            GlyphPicked?.Invoke(this, glyph);
        }

        /// <summary>Raised after a glyph tap (in addition to <see cref="GlyphCommand"/>).</summary>
        public event System.EventHandler<string> GlyphPicked;

        private void EmojiJump_ItemClick(object sender, ItemClickEventArgs e)
        {
            var category = e.ClickedItem as EmojiCategory;
            if (category == null || EmojiCategoryList == null || EmojiZoom == null)
            {
                return;
            }

            _pendingJumpCategory = category;
            if (!EmojiZoom.IsZoomedInViewActive)
            {
                EmojiZoom.IsZoomedInViewActive = true;
                return;
            }

            ScheduleScrollToPendingCategory();
        }

        private void EmojiZoom_ViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView)
            {
                return;
            }

            EmojiCategory pending = _pendingJumpCategory;
            if (pending != null)
            {
                e.DestinationItem.Item = pending;
                return;
            }

            var fromJump = e.SourceItem?.Item as EmojiCategory;
            if (fromJump != null)
            {
                e.DestinationItem.Item = fromJump;
                _pendingJumpCategory = fromJump;
            }
        }

        private void EmojiZoom_ViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView || _pendingJumpCategory == null)
            {
                return;
            }

            ScheduleScrollToPendingCategory();
        }

        private void ScheduleScrollToPendingCategory()
        {
            EmojiCategory category = _pendingJumpCategory;
            if (category == null)
            {
                return;
            }

            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (!ReferenceEquals(_pendingJumpCategory, category))
                {
                    return;
                }

                ScrollToCategory(category);
                _pendingJumpCategory = null;
            });
        }

        private void ScrollToCategory(EmojiCategory category)
        {
            if (category == null || EmojiCategoryList == null)
            {
                return;
            }

            try
            {
                EmojiCategoryList.UpdateLayout();
                EmojiCategoryList.MakeVisible(new SemanticZoomLocation { Item = category });
            }
            catch
            {
            }
        }
    }
}
