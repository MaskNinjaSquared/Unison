using System.ComponentModel;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Composer complement: Emoji / GIF / Stickers. Emoji body is <see cref="EmojiPickerPanel"/>.
    /// </summary>
    public sealed partial class ChatStickerBox : UserControl
    {
        private ChatDetailViewModel _subscribedVm;

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
            return EmojiPanel != null && EmojiPanel.TryConsumeBack();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EmojiPanel?.ResetZoomedIn();
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

            EmojiPanel?.ResetZoomedIn();
        }

        private void OnVisibilityChanged(DependencyObject sender, DependencyProperty dp)
        {
            if (Visibility != Visibility.Visible)
            {
                return;
            }

            EmojiPanel?.ResetZoomedIn();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatDetailViewModel.IsStickerBoxOpen) &&
                ViewModel != null &&
                ViewModel.IsStickerBoxOpen)
            {
                EmojiPanel?.ResetZoomedIn();
            }
            else if (e.PropertyName == nameof(ChatDetailViewModel.IsEmojiCatalogLoading) &&
                     ViewModel != null &&
                     !ViewModel.IsEmojiCatalogLoading)
            {
                EmojiPanel?.ResetZoomedIn();
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

        private void EmojiPanel_GlyphPicked(object sender, string glyph)
        {
            // Place the caret for the next tap without focusing the composer.
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
                vm.UpdateMentionQuery(box.GetPlainText(), caret);
            });
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
