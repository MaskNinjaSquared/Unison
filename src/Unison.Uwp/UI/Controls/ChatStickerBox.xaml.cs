using System.ComponentModel;
using System.Windows.Input;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Composer complement: Emoji / GIF / Stickers with SemanticZoom jump list
    /// (same pattern as Favourites / ContactsVisualizer).
    /// </summary>
    public sealed partial class ChatStickerBox : UserControl
    {
        private ChatDetailViewModel _subscribedVm;
        private EmojiCategory _pendingJumpCategory;
        private CollectionViewSource _groupedSource;

        public ChatStickerBox()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
        }

        private ChatDetailViewModel ViewModel => DataContext as ChatDetailViewModel;

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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            BindEmojiLists();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeVm();
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            UnsubscribeVm();
            _subscribedVm = args.NewValue as ChatDetailViewModel;
            if (_subscribedVm != null)
            {
                _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;
            }

            BindEmojiLists();
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (Visibility != Visibility.Visible)
            {
                return;
            }

            if (EmojiZoom != null)
            {
                EmojiZoom.IsZoomedInViewActive = true;
            }

            BindEmojiLists();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailViewModel.IsStickerBoxOpen) &&
                ViewModel != null &&
                ViewModel.IsStickerBoxOpen)
            {
                if (EmojiZoom != null)
                {
                    EmojiZoom.IsZoomedInViewActive = true;
                }

                BindEmojiLists();
            }
            else if (e.PropertyName == nameof(ChatDetailViewModel.IsEmojiCatalogLoading) &&
                     ViewModel != null &&
                     !ViewModel.IsEmojiCatalogLoading)
            {
                BindEmojiLists();
            }
        }

        private void UnsubscribeVm()
        {
            if (_subscribedVm == null)
            {
                return;
            }

            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedVm = null;
        }

        /// <summary>
        /// Pivot/SemanticZoom often drop inherited ItemsSource bindings; assign from the VM
        /// when the tray opens. Keep the same ObservableCollection while categories stream in.
        /// </summary>
        private void BindEmojiLists()
        {
            ChatDetailViewModel vm = ViewModel;
            if (vm == null)
            {
                return;
            }

            if (EmojiCategoryList != null &&
                (_groupedSource == null || !ReferenceEquals(_groupedSource.Source, vm.EmojiCategories)))
            {
                _groupedSource = new CollectionViewSource
                {
                    IsSourceGrouped = true,
                    ItemsPath = new PropertyPath(nameof(EmojiCategory.Glyphs)),
                    Source = vm.EmojiCategories
                };
                EmojiCategoryList.ItemsSource = _groupedSource.View;
            }

            if (EmojiJumpList != null &&
                !ReferenceEquals(EmojiJumpList.ItemsSource, vm.EmojiCategories))
            {
                EmojiJumpList.ItemsSource = vm.EmojiCategories;
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

            ICommand insert = ViewModel?.InsertEmojiCommand;
            if (insert != null && insert.CanExecute(glyph))
            {
                insert.Execute(glyph);
            }

            // Place the caret for the next tap without focusing the composer. Focus opens the
            // soft keyboard on Mobile while the emoji tray should keep focus.
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ChatDetailViewModel vm = ViewModel;
                if (vm == null || vm.MentionInsertCaret < 0)
                {
                    return;
                }

                ChatComposerInput box = FindMessageInput();
                if (box == null)
                {
                    return;
                }

                int caret = vm.MentionInsertCaret;
                box.CaretIndex = caret;
                // Clears MentionInsertCaret and notes the caret; does not take keyboard focus.
                vm.UpdateMentionQuery(box.GetPlainText(), caret);
            });
        }

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
                // Grouped view: the group (category) is the zoom location; glyph strings repeat
                // across Recents and sections, so ScrollIntoView(glyph) could land on the wrong one.
                EmojiCategoryList.UpdateLayout();
                EmojiCategoryList.MakeVisible(new SemanticZoomLocation { Item = category });
            }
            catch
            {
                // Container may still be unrealized; zoom-in alone is still useful.
            }
        }

        private ChatComposerInput FindMessageInput()
        {
            DependencyObject current = this;
            while (current != null)
            {
                var fe = current as FrameworkElement;
                if (fe != null)
                {
                    var box = fe.FindName("MessageInput") as ChatComposerInput;
                    if (box != null)
                    {
                        return box;
                    }
                }

                current = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
