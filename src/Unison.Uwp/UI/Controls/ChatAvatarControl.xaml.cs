using System;
using System.Linq;
using Unison.Uwp.Services.WhatsApp;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Circular chat avatar with contact/group glyph fallback under the photo.
    /// Used by the chat list and the chat detail header.
    /// </summary>
    public sealed partial class ChatAvatarControl : UserControl
    {
        public static readonly DependencyProperty AvatarUrlProperty =
            DependencyProperty.Register(
                nameof(AvatarUrl),
                typeof(string),
                typeof(ChatAvatarControl),
                new PropertyMetadata(null, OnVisualPropertyChanged));

        public static readonly DependencyProperty IsGroupProperty =
            DependencyProperty.Register(
                nameof(IsGroup),
                typeof(bool),
                typeof(ChatAvatarControl),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        public static readonly DependencyProperty SizeProperty =
            DependencyProperty.Register(
                nameof(Size),
                typeof(double),
                typeof(ChatAvatarControl),
                new PropertyMetadata(48d, OnVisualPropertyChanged));

        public static readonly DependencyProperty ShowBorderProperty =
            DependencyProperty.Register(
                nameof(ShowBorder),
                typeof(bool),
                typeof(ChatAvatarControl),
                new PropertyMetadata(false, OnVisualPropertyChanged));

        /// <summary>What the brush is currently showing, so an unchanged picture is not decoded again.</summary>
        private string _appliedUrl;
        private int _appliedDecodeWidth;

        public ChatAvatarControl()
        {
            InitializeComponent();
            Loaded += (s, e) => ApplyVisual();
        }

        public string AvatarUrl
        {
            get { return (string)GetValue(AvatarUrlProperty); }
            set { SetValue(AvatarUrlProperty, value); }
        }

        public bool IsGroup
        {
            get { return (bool)GetValue(IsGroupProperty); }
            set { SetValue(IsGroupProperty, value); }
        }

        public double Size
        {
            get { return (double)GetValue(SizeProperty); }
            set { SetValue(SizeProperty, value); }
        }

        /// <summary>
        /// When true, draws the themed avatar ring (list + chat header). Off by default
        /// for info panel, settings, and in-bubble contact avatars.
        /// </summary>
        public bool ShowBorder
        {
            get { return (bool)GetValue(ShowBorderProperty); }
            set { SetValue(ShowBorderProperty, value); }
        }

        private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatAvatarControl;
            control?.ApplyVisual();
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
            FallbackEllipse.Width = size;
            FallbackEllipse.Height = size;
            PhotoEllipse.Width = size;
            PhotoEllipse.Height = size;
            BorderEllipse.Width = size;
            BorderEllipse.Height = size;
            BorderEllipse.Visibility = ShowBorder ? Visibility.Visible : Visibility.Collapsed;

            ContactFallbackIcon.FontSize = size * (20d / 48d);
            GroupFallbackHost.Width = size * 0.5;
            GroupFallbackHost.Height = size * 0.5;

            bool group = IsGroup;
            ContactFallbackIcon.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
            GroupFallbackHost.Visibility = group ? Visibility.Visible : Visibility.Collapsed;

            string url = AvatarUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _appliedUrl = null;
                PhotoBrush.ImageSource = null;
                PhotoEllipse.Visibility = Visibility.Collapsed;
                return;
            }

            // Logical units are already scaled by the display, so asking for twice the control
            // size decoded a bitmap four times the area actually drawn.
            int decodeWidth = Math.Max(48, (int)Math.Round(size));

            // ApplyVisual runs for any visual property, and re-decoding an unchanged picture is
            // what made the info panel stutter every time it was laid out.
            if (string.Equals(_appliedUrl, url, StringComparison.Ordinal) &&
                _appliedDecodeWidth == decodeWidth &&
                PhotoBrush.ImageSource != null)
            {
                PhotoEllipse.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                // Order matters: the Uri-taking constructor starts decoding right away, so a
                // DecodePixelWidth assigned afterwards - as an object initializer does - arrives
                // too late and the full-resolution frame is decoded on the UI thread. A group
                // photo at 640 square then cost more than the whole panel around it.
                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = decodeWidth
                };
                bitmap.UriSource = new Uri(url);

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
            PhotoEllipse.Visibility = Visibility.Collapsed;
            PhotoBrush.ImageSource = null;
            _appliedUrl = null;

            string url = AvatarUrl;
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                var whatsApp = App.GetWhatsAppService() as WhatsAppService ?? WhatsAppService.Instance;
                var chat = whatsApp.Chats
                    .FirstOrDefault(c =>
                        string.Equals(c.AvatarUrl, url, StringComparison.Ordinal) ||
                        string.Equals(c.AvatarHighUrl, url, StringComparison.Ordinal) ||
                        string.Equals(c.GetAvatarUrl(false), url, StringComparison.Ordinal) ||
                        string.Equals(c.GetAvatarUrl(true), url, StringComparison.Ordinal));
                if (chat != null)
                {
                    whatsApp.MarkAvatarImageLoadFailed(chat, "ui-brush-failed:" + (e?.ErrorMessage ?? "unknown"));
                }
            }
            catch
            {
            }
        }
    }
}
