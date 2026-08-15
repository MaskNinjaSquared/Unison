using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Single media-grid tile. AdaptiveGridView recycles containers and does not
    /// re-run ItemTemplateSelector, so image/video/audio chrome is applied here from Kind.
    /// </summary>
    public sealed partial class ChatInfoMediaTile : UserControl
    {
        private ChatMessageViewModel _vm;

        public ChatInfoMediaTile()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            Attach(DataContext as ChatMessageViewModel);
            ApplyChrome();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach(DataContext as ChatMessageViewModel);
            ApplyChrome();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Attach(args.NewValue as ChatMessageViewModel);
            ApplyChrome();
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string name = e?.PropertyName;
            if (string.IsNullOrEmpty(name) ||
                name == nameof(ChatMessageViewModel.Kind) ||
                name == nameof(ChatMessageViewModel.IsAudio) ||
                name == nameof(ChatMessageViewModel.IsVideo) ||
                name == nameof(ChatMessageViewModel.IsImage) ||
                name == nameof(ChatMessageViewModel.MediaBadgeDurationText) ||
                name == nameof(ChatMessageViewModel.ShowImageDownloadIcon) ||
                name == nameof(ChatMessageViewModel.IsImageDownloading) ||
                name == nameof(ChatMessageViewModel.ShowVideoDownloadIcon) ||
                name == nameof(ChatMessageViewModel.IsVideoDownloading) ||
                name == nameof(ChatMessageViewModel.ShowAudioDownloadIcon) ||
                name == nameof(ChatMessageViewModel.ShowAudioLoading) ||
                name == nameof(ChatMessageViewModel.ShowAudioPlayButton) ||
                name == nameof(ChatMessageViewModel.ShowAudioPauseButton) ||
                name == nameof(ChatMessageViewModel.NeedsAudioDownload) ||
                name == nameof(ChatMessageViewModel.NeedsVideoDownload) ||
                name == nameof(ChatMessageViewModel.HasLocalAudio) ||
                name == nameof(ChatMessageViewModel.HasLocalVideo))
            {
                ApplyChrome();
            }
        }

        private void Attach(ChatMessageViewModel vm)
        {
            if (ReferenceEquals(_vm, vm))
            {
                return;
            }

            Detach();
            _vm = vm;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmPropertyChanged;
            }
        }

        private void Detach()
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = null;
            }
        }

        private void ApplyChrome()
        {
            if (Root == null)
            {
                return;
            }

            var vm = _vm;
            bool audio = IsAudioItem(vm);
            bool video = !audio && IsVideoItem(vm);

            Root.Background = new SolidColorBrush(video
                ? Windows.UI.Color.FromArgb(0xFF, 0x11, 0x11, 0x11)
                : Windows.UI.Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A));

            if (Thumb != null)
            {
                Thumb.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
            }

            if (ImageGlyph != null)
            {
                ImageGlyph.Visibility = (!audio && !video) ? Visibility.Visible : Visibility.Collapsed;
            }

            bool busy = vm != null && (
                (!audio && !video && vm.IsImageDownloading) ||
                (video && vm.IsVideoDownloading) ||
                (audio && vm.ShowAudioLoading));

            if (PlayHost != null)
            {
                PlayHost.Visibility = (audio || video) && !busy
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            bool needsDownload = vm != null && (
                (audio && vm.ShowAudioDownloadIcon) ||
                (video && vm.ShowVideoDownloadIcon));
            bool showPause = audio && vm != null && vm.ShowAudioPauseButton;
            bool showPlay = (audio || video) && !needsDownload && !showPause;

            SetGlyphVisible(DownloadGlyph, needsDownload);
            SetGlyphVisible(PlayGlyph, showPlay);
            SetGlyphVisible(PauseGlyph, showPause);

            if (Footer != null)
            {
                Footer.Visibility = (audio || video) ? Visibility.Visible : Visibility.Collapsed;
            }

            if (FooterIcon != null)
            {
                FooterIcon.Text = audio ? "\uE8D6" : "\uE714";
            }

            if (FooterDuration != null)
            {
                FooterDuration.Text = vm != null ? (vm.MediaBadgeDurationText ?? string.Empty) : string.Empty;
            }

            bool imageDownload = !audio && !video && vm != null && vm.ShowImageDownloadIcon;
            if (DownloadImageButton != null)
            {
                DownloadImageButton.Visibility = imageDownload ? Visibility.Visible : Visibility.Collapsed;
            }

            if (BusyRing != null)
            {
                BusyRing.IsActive = busy;
                BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static bool IsAudioItem(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return false;
            }

            if (vm.IsAudio)
            {
                return true;
            }

            return vm.Kind == ChatMessageKind.Audio || vm.Kind == ChatMessageKind.Voice;
        }

        private static bool IsVideoItem(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return false;
            }

            return vm.IsVideo || vm.Kind == ChatMessageKind.Video;
        }

        private static void SetGlyphVisible(TextBlock glyph, bool visible)
        {
            if (glyph != null)
            {
                glyph.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
