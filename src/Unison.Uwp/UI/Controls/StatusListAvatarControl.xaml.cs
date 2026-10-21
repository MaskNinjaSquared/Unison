using System;
using Unison.Core.Models;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Status list circle: thumb/avatar with a green (Recent) or gray (Viewed) ring.
    /// Photo path mirrors <see cref="ChatAvatarControl"/> (Ellipse + ImageBrush UniformToFill)
    /// so landscape video thumbs cover and crop on Creators Update the same way chat avatars do.
    /// </summary>
    public sealed partial class StatusListAvatarControl : UserControl
    {
        private static readonly SolidColorBrush RecentRingBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xD3, 0x66));

        private static readonly SolidColorBrush ViewedRingBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

        public static readonly DependencyProperty AvatarUrlProperty =
            DependencyProperty.Register(
                nameof(AvatarUrl),
                typeof(string),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(null, OnAvatarUrlChanged));

        public static readonly DependencyProperty ViewStateProperty =
            DependencyProperty.Register(
                nameof(ViewState),
                typeof(StatusViewState),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(StatusViewState.Recent, OnViewStateChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(StatusListAvatarControl),
                new PropertyMetadata(56d, OnSizeChanged));

        /// <summary>What the brush is showing, so an unchanged picture is not decoded again.</summary>
        private string _appliedUrl;
        private int _appliedDecodeWidth;

        public StatusListAvatarControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyAll();
        }

        public string AvatarUrl
        {
            get { return (string)GetValue(AvatarUrlProperty); }
            set { SetValue(AvatarUrlProperty, value); }
        }

        public StatusViewState ViewState
        {
            get { return (StatusViewState)GetValue(ViewStateProperty); }
            set { SetValue(ViewStateProperty, value); }
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        private static void OnAvatarUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplyPhoto();
            }
        }

        private static void OnViewStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplyRing();
            }
        }

        private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as StatusListAvatarControl;
            if (control != null)
            {
                control.ApplySize();
            }
        }

        private void ApplyAll()
        {
            ApplySize();
            ApplyRing();
            ApplyPhoto();
        }

        private void ApplyPhoto()
        {
            if (PhotoBrush == null || PhotoEllipse == null)
            {
                return;
            }

            // Glyph fallback only — do not route status thumbs through ChatAvatarControl
            // (keeps one photo path: Ellipse/ImageBrush, same as the chat list).
            if (InnerAvatar != null)
            {
                InnerAvatar.AvatarUrl = null;
            }

            string url = AvatarUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _appliedUrl = null;
                PhotoBrush.ImageSource = null;
                PhotoEllipse.Visibility = Visibility.Collapsed;
                return;
            }

            // Ellipse must already be sized (ApplySize) before the brush measures UniformToFill.
            double inner = PhotoEllipse.Width > 0 ? PhotoEllipse.Width : 48d;
            int decodeWidth = Math.Max(48, (int)Math.Round(inner));

            if (string.Equals(_appliedUrl, url, StringComparison.Ordinal) &&
                _appliedDecodeWidth == decodeWidth &&
                PhotoBrush.ImageSource != null)
            {
                PhotoBrush.Stretch = Stretch.UniformToFill;
                PhotoBrush.AlignmentX = AlignmentX.Center;
                PhotoBrush.AlignmentY = AlignmentY.Center;
                PhotoEllipse.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                // Same order as ChatAvatarControl: DecodePixel* before UriSource, then Stretch.
                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = decodeWidth
                };
                bitmap.UriSource = new Uri(url);

                PhotoBrush.Stretch = Stretch.UniformToFill;
                PhotoBrush.AlignmentX = AlignmentX.Center;
                PhotoBrush.AlignmentY = AlignmentY.Center;
                PhotoBrush.ImageSource = bitmap;
                PhotoEllipse.Visibility = Visibility.Visible;
                _appliedUrl = url;
                _appliedDecodeWidth = decodeWidth;
            }
            catch
            {
                _appliedUrl = null;
                PhotoBrush.ImageSource = null;
                PhotoEllipse.Visibility = Visibility.Collapsed;
            }
        }

        private void PhotoBrush_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _appliedUrl = null;
            if (PhotoBrush != null)
            {
                PhotoBrush.ImageSource = null;
            }

            if (PhotoEllipse != null)
            {
                PhotoEllipse.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyRing()
        {
            if (Ring == null)
            {
                return;
            }

            Ring.Stroke = ViewState == StatusViewState.Viewed
                ? ViewedRingBrush
                : RecentRingBrush;
        }

        private void ApplySize()
        {
            double outer = Size > 0 ? Size : 56d;
            double inner = Math.Max(24d, outer - 8d);
            if (RootGrid != null)
            {
                RootGrid.Width = outer;
                RootGrid.Height = outer;
            }

            if (Ring != null)
            {
                Ring.Width = outer;
                Ring.Height = outer;
            }

            if (InnerAvatar != null)
            {
                InnerAvatar.Width = inner;
                InnerAvatar.Height = inner;
                InnerAvatar.Size = inner;
            }

            if (PhotoEllipse != null)
            {
                PhotoEllipse.Width = inner;
                PhotoEllipse.Height = inner;
            }

            if (!string.IsNullOrWhiteSpace(AvatarUrl))
            {
                ApplyPhoto();
            }
        }
    }
}
