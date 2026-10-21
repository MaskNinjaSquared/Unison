using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Community “Avisos” avatar: blue plate + optional group photo (placeholder until a
    /// dedicated asset lands).
    /// </summary>
    public sealed partial class CommunityAnnouncementsAvatarControl : UserControl
    {
        public static readonly DependencyProperty AvatarUrlProperty =
            DependencyProperty.Register(
                nameof(AvatarUrl),
                typeof(string),
                typeof(CommunityAnnouncementsAvatarControl),
                new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(CommunityAnnouncementsAvatarControl),
                new PropertyMetadata(48d, OnVisualPropertyChanged));

        public static readonly DependencyProperty ShowBorderProperty =
            DependencyProperty.Register(
                nameof(ShowBorder),
                typeof(bool),
                typeof(CommunityAnnouncementsAvatarControl),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        private string _appliedUrl;
        private int _appliedDecodeWidth;

        public CommunityAnnouncementsAvatarControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyVisual();
        }

        public string AvatarUrl
        {
            get { return (string)GetValue(AvatarUrlProperty); }
            set { SetValue(AvatarUrlProperty, value); }
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        public bool ShowBorder
        {
            get { return (bool)GetValue(ShowBorderProperty); }
            set { SetValue(ShowBorderProperty, value); }
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as CommunityAnnouncementsAvatarControl)?.ApplyVisual();
        }

        private void ApplyVisual()
        {
            if (RootGrid == null)
            {
                return;
            }

            double size = Size > 0 ? Size : 48d;
            RootGrid.Width = size;
            RootGrid.Height = size;
            FallbackPlate.Width = size;
            FallbackPlate.Height = size;
            PhotoEllipse.Width = size;
            PhotoEllipse.Height = size;
            BorderEllipse.Width = size;
            BorderEllipse.Height = size;
            MegaphoneFallback.FontSize = Math.Max(12, size * 0.42);
            BorderEllipse.Visibility = ShowBorder ? Visibility.Visible : Visibility.Collapsed;

            string url = AvatarUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                ClearPhoto();
                return;
            }

            int decode = (int)Math.Ceiling(size * 2);
            if (string.Equals(_appliedUrl, url, StringComparison.Ordinal) &&
                _appliedDecodeWidth == decode &&
                PhotoEllipse.Visibility == Visibility.Visible)
            {
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.DecodePixelWidth = decode;
                bitmap.UriSource = new Uri(url);
                PhotoBrush.ImageSource = bitmap;
                PhotoEllipse.Visibility = Visibility.Visible;
                MegaphoneFallback.Visibility = Visibility.Collapsed;
                _appliedUrl = url;
                _appliedDecodeWidth = decode;
            }
            catch
            {
                ClearPhoto();
            }
        }

        private void ClearPhoto()
        {
            PhotoBrush.ImageSource = null;
            PhotoEllipse.Visibility = Visibility.Collapsed;
            MegaphoneFallback.Visibility = Visibility.Visible;
            _appliedUrl = null;
            _appliedDecodeWidth = 0;
        }

        private void PhotoBrush_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ClearPhoto();
        }
    }
}
