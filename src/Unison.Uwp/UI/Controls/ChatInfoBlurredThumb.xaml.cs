using System;
using Unison.Core.ViewModels;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>Low-quality jpegThumbnail / cached URI for the chat-info media grid.</summary>
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
                name == nameof(ChatMessageViewModel.InfoPreviewBase64) ||
                name == nameof(ChatMessageViewModel.HasInfoPreviewUri) ||
                name == nameof(ChatMessageViewModel.HasInfoPreviewBase64) ||
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

        private async System.Threading.Tasks.Task LoadPreviewAsync()
        {
            int version = ++_loadVersion;
            var vm = _vm;
            if (PreviewImage == null)
            {
                return;
            }

            if (vm == null)
            {
                PreviewImage.Source = null;
                return;
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
                    if (version != _loadVersion)
                    {
                        return;
                    }

                    PreviewImage.Source = bmp;
                    return;
                }

                string encoded = vm.InfoPreviewBase64;
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    if (version == _loadVersion)
                    {
                        PreviewImage.Source = null;
                    }

                    return;
                }

                byte[] bytes = Convert.FromBase64String(encoded);
                var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var image = new BitmapImage
                {
                    DecodePixelWidth = 48,
                    DecodePixelType = DecodePixelType.Logical
                };
                await image.SetSourceAsync(stream);
                if (version != _loadVersion)
                {
                    return;
                }

                PreviewImage.Source = image;
            }
            catch (Exception)
            {
                if (version == _loadVersion)
                {
                    PreviewImage.Source = null;
                }
            }
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
