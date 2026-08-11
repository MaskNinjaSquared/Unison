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

            ContactFallbackIcon.FontSize = size * (20d / 48d);
            GroupFallbackHost.Width = size * 0.5;
            GroupFallbackHost.Height = size * 0.5;

            bool group = IsGroup;
            ContactFallbackIcon.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
            GroupFallbackHost.Visibility = group ? Visibility.Visible : Visibility.Collapsed;

            string url = AvatarUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                PhotoBrush.ImageSource = null;
                PhotoEllipse.Visibility = Visibility.Collapsed;
                return;
            }

            try
            {
                int decodeWidth = Math.Max(48, (int)Math.Round(size * 2));
                PhotoBrush.ImageSource = new BitmapImage(new Uri(url))
                {
                    DecodePixelWidth = decodeWidth,
                    DecodePixelType = DecodePixelType.Logical
                };
                PhotoEllipse.Visibility = Visibility.Visible;
            }
            catch
            {
                PhotoBrush.ImageSource = null;
                PhotoEllipse.Visibility = Visibility.Collapsed;
            }
        }

        private void PhotoBrush_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            PhotoEllipse.Visibility = Visibility.Collapsed;
            PhotoBrush.ImageSource = null;

            string url = AvatarUrl;
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            try
            {
                var whatsApp = App.GetWhatsAppService() as WhatsAppService ?? WhatsAppService.Instance;
                var chat = whatsApp.Chats
                    .FirstOrDefault(c => string.Equals(c.AvatarUrl, url, StringComparison.Ordinal));
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
