using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Uwp.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class TiledBackground : UserControl
    {
        private const int TileSize = 408;
        private readonly BitmapImage _sharedTileSource;
        private int _lastColumns = -1;
        private int _lastRows = -1;

        public TiledBackground()
        {
            InitializeComponent();

            // Underfill while tiles decode — theme brush (Unison #032D34 / WhatsApp #2C2C2C).
            if (Application.Current.Resources.TryGetValue("ChatDetailWallpaperBackgroundBrush", out object brushObj)
                && brushObj is Brush themeBrush)
            {
                TileCanvas.Background = themeBrush;
            }
            else
            {
                TileCanvas.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2C, 0x2C, 0x2C));
            }

            // Phone screens only need a handful of 408px tiles; solid #121B22 looked wrong.
            int decodeWidth = TileSize;
            try
            {
                var systemInfo = App.Services?.GetService<ISystemInfoProvider>();
                bool mobile = systemInfo != null
                    ? systemInfo.IsMobile()
                    : SystemInfoProvider.DetectIsMobile();
                if (mobile)
                {
                    decodeWidth = 256;
                }
            }
            catch
            {
            }

            _sharedTileSource = new BitmapImage(new Uri("ms-appx:///Assets/Backgrounds/WhatsAppBackground_Colored.png"))
            {
                DecodePixelWidth = decodeWidth,
                DecodePixelType = DecodePixelType.Physical
            };

            SizeChanged += TiledBackground_SizeChanged;
            Unloaded += TiledBackground_Unloaded;
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
        }

        private void RebuildTiles()
        {
            if (ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            int columns = (int)Math.Ceiling(ActualWidth / TileSize) + 1;
            int rows = (int)Math.Ceiling(ActualHeight / TileSize) + 1;

            // Teclado virtual e pequenas mudancas de layout geravam dezenas de rebuilds
            // iguais. So recriamos os elementos quando a grade realmente mudou.
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
                    var image = new Image
                    {
                        Source = _sharedTileSource,
                        Width = TileSize,
                        Height = TileSize,
                        Stretch = Stretch.UniformToFill
                    };

                    Canvas.SetLeft(image, column * TileSize);
                    Canvas.SetTop(image, row * TileSize);
                    TileCanvas.Children.Add(image);
                }
            }
        }
    }
}
