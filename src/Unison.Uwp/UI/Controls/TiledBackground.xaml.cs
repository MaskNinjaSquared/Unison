using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Uwp.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Repeats a wallpaper as a grid of Images. Cell aspect follows the decoded asset so
    /// UniformToFill is not used (presets must tile, not grow to fill the chat).
    /// </summary>
    public sealed partial class TiledBackground : UserControl
    {
        /// <summary>Fallback aspect when PixelWidth/Height are not ready yet (Default doodle panel).</summary>
        private const double FallbackAssetWidth = 440;
        private const double FallbackAssetHeight = 952;

        /// <summary>DIP width of each laid-out tile (desktop and Mobile — motif size on screen).</summary>
        private const double TileWidthDip = 408;

        /// <summary>
        /// Mobile-only decode cap in physical pixels. Keeps RAM down on W10M; the Image still
        /// draws at <see cref="TileWidthDip"/>. Do not reuse this as the layout cell size.
        /// </summary>
        private const int MobileDecodeWidthPhysical = 256;

        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register(
                nameof(SourceUri),
                typeof(string),
                typeof(TiledBackground),
                new PropertyMetadata(ChatBackgroundCatalog.DefaultAssetUri, OnSourceUriChanged));

        private BitmapImage _sharedTileSource;
        private readonly double _tileWidth;
        private readonly int _decodeWidth;
        private double _tileHeight;
        private int _lastColumns = -1;
        private int _lastRows = -1;
        private string _loadedUri;

        public TiledBackground()
        {
            InitializeComponent();

            // Transparent so ChatBackgroundSurface.Underfill (theme or solid color) shows
            // through the doodle / preset tiles.
            TileCanvas.Background = null;

            _tileWidth = TileWidthDip;
            _tileHeight = TileWidthDip * (FallbackAssetHeight / FallbackAssetWidth);
            _decodeWidth = ResolveDecodeWidth();

            SizeChanged += TiledBackground_SizeChanged;
            Unloaded += TiledBackground_Unloaded;
            Loaded += (_, __) => ApplySourceUri();
        }

        private static int ResolveDecodeWidth()
        {
            try
            {
                var systemInfo = App.Services?.GetService<ISystemInfoProvider>();
                bool mobile = systemInfo != null
                    ? systemInfo.IsMobile()
                    : SystemInfoProvider.DetectIsMobile();
                if (mobile)
                {
                    return MobileDecodeWidthPhysical;
                }
            }
            catch
            {
            }

            return (int)TileWidthDip;
        }

        /// <summary>ms-appx / ms-appdata URI of the tile asset.</summary>
        public string SourceUri
        {
            get { return (string)GetValue(SourceUriProperty); }
            set { SetValue(SourceUriProperty, value); }
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as TiledBackground;
            control?.ApplySourceUri();
        }

        private void ApplySourceUri()
        {
            string uri = SourceUri;
            if (string.IsNullOrWhiteSpace(uri))
            {
                uri = ChatBackgroundCatalog.DefaultAssetUri;
            }

            if (string.Equals(_loadedUri, uri, StringComparison.OrdinalIgnoreCase) &&
                _sharedTileSource != null)
            {
                return;
            }

            _loadedUri = uri;
            _lastColumns = -1;
            _lastRows = -1;
            TileCanvas.Children.Clear();

            // UriSource last: DecodePixel* must be set before decode starts.
            _sharedTileSource = new BitmapImage
            {
                DecodePixelType = DecodePixelType.Physical,
                DecodePixelWidth = _decodeWidth
            };
            _sharedTileSource.ImageOpened += SharedTileSource_ImageOpened;
            _sharedTileSource.ImageFailed += SharedTileSource_ImageFailed;
            try
            {
                _sharedTileSource.UriSource = new Uri(uri);
            }
            catch
            {
                _sharedTileSource = null;
                _loadedUri = null;
            }

            RebuildTiles();
        }

        private void SharedTileSource_ImageOpened(object sender, RoutedEventArgs e)
        {
            var bmp = sender as BitmapImage;
            if (bmp != null && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
            {
                _tileHeight = _tileWidth * ((double)bmp.PixelHeight / bmp.PixelWidth);
            }

            _lastColumns = -1;
            _lastRows = -1;
            RebuildTiles();
        }

        private void SharedTileSource_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[TiledBackground] load failed: " + e.ErrorMessage);
        }

        private void TiledBackground_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RebuildTiles();
        }

        private void TiledBackground_Unloaded(object sender, RoutedEventArgs e)
        {
            TileCanvas.Children.Clear();
            _lastColumns = -1;
            _lastRows = -1;
            if (_sharedTileSource != null)
            {
                _sharedTileSource.ImageOpened -= SharedTileSource_ImageOpened;
                _sharedTileSource.ImageFailed -= SharedTileSource_ImageFailed;
            }
        }

        private void RebuildTiles()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0 || _sharedTileSource == null)
            {
                return;
            }

            int columns = (int)Math.Ceiling(ActualWidth / _tileWidth) + 1;
            int rows = (int)Math.Ceiling(ActualHeight / _tileHeight) + 1;

            if (columns == _lastColumns && rows == _lastRows)
            {
                return;
            }

            _lastColumns = columns;
            _lastRows = rows;
            TileCanvas.Children.Clear();

            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    // Fill: cell aspect matches the asset, so the full panel repeats without crop.
                    var image = new Image
                    {
                        Source = _sharedTileSource,
                        Width = _tileWidth,
                        Height = _tileHeight,
                        Stretch = Stretch.Fill
                    };

                    Canvas.SetLeft(image, column * _tileWidth);
                    Canvas.SetTop(image, row * _tileHeight);
                    TileCanvas.Children.Add(image);
                }
            }
        }
    }
}
