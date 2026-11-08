using System;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Channel create form overlay: round avatar, fields, profile-photo flyout, emoji dock.</summary>
    public sealed partial class CreateChannelFormOverlay : UserControl
    {
        private NewConversationViewModel _hookedVm;
        private RichLabeledTextBox _emojiTarget;
        private bool _photoButtonHooked;

        public CreateChannelFormOverlay()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Closes the emoji dock / consumes back while the picker is open.</summary>
        public bool TryHandleBack()
        {
            if (ChannelEmojiDock == null || ChannelEmojiDock.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ChannelEmojiPanel != null && ChannelEmojiPanel.TryConsumeBack())
            {
                return true;
            }

            ChannelEmojiDock.Visibility = Visibility.Collapsed;
            _emojiTarget = null;
            return true;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookPhotoButton();
            HookViewModel(DataContext as NewConversationViewModel);
            _ = ApplyPhotoPreviewAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnhookPhotoButton();
            UnhookViewModel();
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            HookViewModel(args.NewValue as NewConversationViewModel);
            _ = ApplyPhotoPreviewAsync();
        }

        private void HookViewModel(NewConversationViewModel vm)
        {
            if (ReferenceEquals(_hookedVm, vm))
            {
                return;
            }

            UnhookViewModel();
            _hookedVm = vm;
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void UnhookViewModel()
        {
            if (_hookedVm != null)
            {
                _hookedVm.PropertyChanged -= ViewModel_PropertyChanged;
                _hookedVm = null;
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e != null &&
                string.Equals(e.PropertyName, nameof(NewConversationViewModel.ChannelPhotoBytes), StringComparison.Ordinal))
            {
                _ = ApplyPhotoPreviewAsync();
            }
        }

        private void HookPhotoButton()
        {
            if (_photoButtonHooked || ChannelPhotoButton == null)
            {
                return;
            }

            ChannelPhotoButton.CameraRequested += PhotoButton_CameraRequested;
            ChannelPhotoButton.GalleryRequested += PhotoButton_GalleryRequested;
            ChannelPhotoButton.DeleteRequested += PhotoButton_DeleteRequested;
            _photoButtonHooked = true;
        }

        private void UnhookPhotoButton()
        {
            if (!_photoButtonHooked || ChannelPhotoButton == null)
            {
                return;
            }

            ChannelPhotoButton.CameraRequested -= PhotoButton_CameraRequested;
            ChannelPhotoButton.GalleryRequested -= PhotoButton_GalleryRequested;
            ChannelPhotoButton.DeleteRequested -= PhotoButton_DeleteRequested;
            _photoButtonHooked = false;
        }

        private void PhotoButton_CameraRequested(object sender, EventArgs e)
        {
            var vm = _hookedVm ?? DataContext as NewConversationViewModel;
            if (vm?.PickChannelPhotoFromCameraCommand != null &&
                vm.PickChannelPhotoFromCameraCommand.CanExecute(null))
            {
                vm.PickChannelPhotoFromCameraCommand.Execute(null);
            }
        }

        private void PhotoButton_GalleryRequested(object sender, EventArgs e)
        {
            var vm = _hookedVm ?? DataContext as NewConversationViewModel;
            if (vm?.PickChannelPhotoCommand != null && vm.PickChannelPhotoCommand.CanExecute(null))
            {
                vm.PickChannelPhotoCommand.Execute(null);
            }
        }

        private void PhotoButton_DeleteRequested(object sender, EventArgs e)
        {
            var vm = _hookedVm ?? DataContext as NewConversationViewModel;
            if (vm != null)
            {
                vm.ChannelPhotoBytes = null;
            }
        }

        private void ChannelField_EmojiInvoked(object sender, EventArgs e)
        {
            var field = sender as RichLabeledTextBox;
            if (field == null || ChannelEmojiDock == null)
            {
                return;
            }

            bool sameTarget = ReferenceEquals(_emojiTarget, field);
            bool isOpen = ChannelEmojiDock.Visibility == Visibility.Visible;
            if (isOpen && sameTarget)
            {
                ChannelEmojiDock.Visibility = Visibility.Collapsed;
                _emojiTarget = null;
                return;
            }

            if (ChannelEmojiPanel != null && ChannelEmojiPanel.CategoriesSource == null)
            {
                ChannelEmojiPanel.CategoriesSource = EmojiCatalog.BuiltInCategoriesFor(DeviceCategory.High);
            }

            _emojiTarget = field;
            ChannelEmojiDock.Visibility = Visibility.Visible;
            field.FocusInput();
        }

        private void ChannelEmojiPanel_GlyphPicked(object sender, string glyph)
        {
            if (_emojiTarget == null || string.IsNullOrEmpty(glyph))
            {
                return;
            }

            _emojiTarget.InsertEmoji(glyph);
        }

        private async System.Threading.Tasks.Task ApplyPhotoPreviewAsync()
        {
            if (ChannelPhotoBrush == null)
            {
                return;
            }

            byte[] bytes = (_hookedVm ?? DataContext as NewConversationViewModel)?.ChannelPhotoBytes;
            if (bytes == null || bytes.Length == 0)
            {
                ChannelPhotoBrush.ImageSource = null;
                return;
            }

            try
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 320 };
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                }

                ChannelPhotoBrush.ImageSource = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[CreateChannelFormOverlay] Photo preview: " + ex.Message);
                ChannelPhotoBrush.ImageSource = null;
            }
        }
    }
}
