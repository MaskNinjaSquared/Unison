using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.UWPApp.UI.Controls
{
    public sealed partial class TiledBackground : UserControl
    {
        private const int TileSize = 408; // Size of the WhatsApp doodle tile

        public TiledBackground()
        {
            this.InitializeComponent();
            this.SizeChanged += TiledBackground_SizeChanged;
        }

        private void TiledBackground_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RebuildTiles();
        }

        private void RebuildTiles()
        {
            TileCanvas.Children.Clear();

            if (ActualWidth <= 0 || ActualHeight <= 0) return;

            int cols = (int)System.Math.Ceiling(ActualWidth / TileSize) + 1;
            int rows = (int)System.Math.Ceiling(ActualHeight / TileSize) + 1;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    var image = new Image
                    {
                        Source = new BitmapImage(new System.Uri("ms-appx:///Assets/Backgrounds/WhatsAppBackground_Colored.png")),
                        Width = TileSize,
                        Height = TileSize,
                        Stretch = Stretch.UniformToFill,
                        Opacity = 1.0 // Use full opacity as it's already recolored to #353535
                    };

                    Canvas.SetLeft(image, col * TileSize);
                    Canvas.SetTop(image, row * TileSize);
                    TileCanvas.Children.Add(image);
                }
            }
        }
    }
}
