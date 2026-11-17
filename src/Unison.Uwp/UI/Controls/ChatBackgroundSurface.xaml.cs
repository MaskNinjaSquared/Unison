// =============================================================================
// ChatBackgroundSurface
//
// Fill mode from ChatBackgroundCatalog: Tile (Default doodles + Cubes/Owls/Damask),
// Stretch (Custom only), None (DefaultNoDoodles underfill).
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class ChatBackgroundSurface : UserControl
    {
        public static readonly DependencyProperty BackgroundKindProperty =
            DependencyProperty.Register(
                nameof(BackgroundKind),
                typeof(ChatBackgroundKind),
                typeof(ChatBackgroundSurface),
                new PropertyMetadata(ChatBackgroundKind.Default, OnBackgroundChanged));

        public static readonly DependencyProperty CustomPathProperty =
            DependencyProperty.Register(
                nameof(CustomPath),
                typeof(string),
                typeof(ChatBackgroundSurface),
                new PropertyMetadata(null, OnBackgroundChanged));

        public static readonly DependencyProperty ImageUriProperty =
            DependencyProperty.Register(
                nameof(ImageUri),
                typeof(string),
                typeof(ChatBackgroundSurface),
                new PropertyMetadata(null, OnBackgroundChanged));

        public static readonly DependencyProperty SolidColorHexProperty =
            DependencyProperty.Register(
                nameof(SolidColorHex),
                typeof(string),
                typeof(ChatBackgroundSurface),
                new PropertyMetadata(null, OnBackgroundChanged));

        public ChatBackgroundSurface()
        {
            InitializeComponent();
            Loaded += (_, __) => ApplyWallpaper();
        }

        public ChatBackgroundKind BackgroundKind
        {
            get { return (ChatBackgroundKind)GetValue(BackgroundKindProperty); }
            set { SetValue(BackgroundKindProperty, value); }
        }

        public string CustomPath
        {
            get { return (string)GetValue(CustomPathProperty); }
            set { SetValue(CustomPathProperty, value); }
        }

        public string ImageUri
        {
            get { return (string)GetValue(ImageUriProperty); }
            set { SetValue(ImageUriProperty, value); }
        }

        /// <summary>#AARRGGBB underfill override for Default family; null = theme brush.</summary>
        public string SolidColorHex
        {
            get { return (string)GetValue(SolidColorHexProperty); }
            set { SetValue(SolidColorHexProperty, value); }
        }

        private static void OnBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var surface = d as ChatBackgroundSurface;
            surface?.ApplyWallpaper();
        }

        private void ApplyWallpaper()
        {
            if (DefaultTiles == null || WallpaperImage == null || Underfill == null)
            {
                return;
            }

            ApplyUnderfill();

            ChatBackgroundFillMode mode = ResolveFillMode();
            if (mode == ChatBackgroundFillMode.Tile)
            {
                string tileUri = ResolveTileUri();
                if (!string.IsNullOrWhiteSpace(tileUri))
                {
                    DefaultTiles.SourceUri = tileUri;
                }

                DefaultTiles.Visibility = Visibility.Visible;
                WallpaperImage.Visibility = Visibility.Collapsed;
                WallpaperImage.Source = null;
                return;
            }

            DefaultTiles.Visibility = Visibility.Collapsed;

            if (mode == ChatBackgroundFillMode.None)
            {
                WallpaperImage.Visibility = Visibility.Collapsed;
                WallpaperImage.Source = null;
                return;
            }

            // Stretch — Custom (or explicit ImageUri override).
            WallpaperImage.Visibility = Visibility.Visible;
            string uri = ResolveStretchUri();
            if (string.IsNullOrWhiteSpace(uri))
            {
                WallpaperImage.Visibility = Visibility.Collapsed;
                WallpaperImage.Source = null;
                return;
            }

            try
            {
                WallpaperImage.Source = new BitmapImage(new Uri(uri));
            }
            catch
            {
                DefaultTiles.SourceUri = ChatBackgroundCatalog.DefaultAssetUri;
                DefaultTiles.Visibility = Visibility.Visible;
                WallpaperImage.Visibility = Visibility.Collapsed;
                WallpaperImage.Source = null;
            }
        }

        private void ApplyUnderfill()
        {
            if (!string.IsNullOrWhiteSpace(SolidColorHex) &&
                ChatBackgroundCatalog.IsDefaultFamily(BackgroundKind))
            {
                try
                {
                    Underfill.Background = new SolidColorBrush(ParseHex(SolidColorHex));
                    return;
                }
                catch
                {
                }
            }

            if (Application.Current.Resources.TryGetValue(
                    "ChatDetailWallpaperBackgroundBrush",
                    out object brushObj) &&
                brushObj is Brush themeBrush)
            {
                Underfill.Background = themeBrush;
            }
        }

        private static Windows.UI.Color ParseHex(string hex)
        {
            string value = hex.Trim();
            if (value.StartsWith("#", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (value.Length == 6)
            {
                value = "FF" + value;
            }

            byte a = Convert.ToByte(value.Substring(0, 2), 16);
            byte r = Convert.ToByte(value.Substring(2, 2), 16);
            byte g = Convert.ToByte(value.Substring(4, 2), 16);
            byte b = Convert.ToByte(value.Substring(6, 2), 16);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }

        private ChatBackgroundFillMode ResolveFillMode()
        {
            // Explicit URI always stretches (host override).
            if (!string.IsNullOrWhiteSpace(ImageUri))
            {
                return ChatBackgroundFillMode.Stretch;
            }

            if (BackgroundKind == ChatBackgroundKind.Custom &&
                string.IsNullOrWhiteSpace(CustomPath))
            {
                // Custom without a file yet → fall back to tiled Default doodles.
                return ChatBackgroundFillMode.Tile;
            }

            return ChatBackgroundCatalog.GetFillMode(BackgroundKind);
        }

        private string ResolveTileUri()
        {
            if (BackgroundKind == ChatBackgroundKind.Custom &&
                string.IsNullOrWhiteSpace(CustomPath))
            {
                return ChatBackgroundCatalog.DefaultAssetUri;
            }

            return ChatBackgroundCatalog.ResolveImageUri(BackgroundKind, null)
                   ?? ChatBackgroundCatalog.DefaultAssetUri;
        }

        private string ResolveStretchUri()
        {
            if (!string.IsNullOrWhiteSpace(ImageUri))
            {
                return ImageUri;
            }

            if (BackgroundKind == ChatBackgroundKind.Custom &&
                !string.IsNullOrWhiteSpace(CustomPath))
            {
                string path = CustomPath.Replace('\\', '/').TrimStart('/');
                return "ms-appdata:///local/" + path;
            }

            return ChatBackgroundCatalog.ResolveImageUri(BackgroundKind, null);
        }
    }
}
