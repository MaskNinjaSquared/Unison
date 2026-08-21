using System;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Cached local URI for the chat-info media grid (full media or protocol thumb).</summary>
    public sealed partial class ChatInfoBlurredThumb : UserControl
    {
        private ChatMessageViewModel _vm;
        private int _loadVersion;

        public ChatInfoBlurredThumb()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
            Loaded += OnLoaded;
            Attach(DataContext as ChatMessageViewModel);
            _ = LoadPreviewAsync();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Attach(DataContext as ChatMessageViewModel);
            _ = LoadPreviewAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Attach(args.NewValue as ChatMessageViewModel);
            _ = LoadPreviewAsync();
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string name = e?.PropertyName;
            if (string.IsNullOrEmpty(name) ||
                name == nameof(ChatMessageViewModel.InfoPreviewUri) ||
                name == nameof(ChatMessageViewModel.HasInfoPreviewUri) ||
                name == nameof(ChatMessageViewModel.ImageUri) ||
                name == nameof(ChatMessageViewModel.VideoPosterUri))
            {
                _ = LoadPreviewAsync();
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

        private System.Threading.Tasks.Task LoadPreviewAsync()
        {
            int version = ++_loadVersion;
            var vm = _vm;
            if (PreviewImage == null)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            if (vm == null)
            {
                PreviewImage.Source = null;
                return System.Threading.Tasks.Task.CompletedTask;
            }

            try
            {
                string uri = vm.InfoPreviewUri;
                if (IsLoadableUri(uri))
                {
                    var bmp = new BitmapImage
                    {
                        DecodePixelWidth = 160,
                        DecodePixelType = DecodePixelType.Logical,
                        UriSource = new Uri(uri)
                    };
                    if (version == _loadVersion)
                    {
                        PreviewImage.Source = bmp;
                    }

                    return System.Threading.Tasks.Task.CompletedTask;
                }

                if (version == _loadVersion)
                {
                    PreviewImage.Source = null;
                }
            }
            catch (Exception)
            {
                if (version == _loadVersion)
                {
                    PreviewImage.Source = null;
                }
            }

            return System.Threading.Tasks.Task.CompletedTask;
        }

        private static bool IsLoadableUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }

            return uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                   uri.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase) ||
                   uri.StartsWith("ms-appdata", StringComparison.OrdinalIgnoreCase);
        }
    }
}
