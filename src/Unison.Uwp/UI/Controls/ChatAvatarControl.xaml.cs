using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
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
            FallbackPlate.Width = size;
            FallbackPlate.Height = size;
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
            ApplyFallbackVisual(group);

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

        /// <summary>
        /// WhatsApp Legacy registers full-bleed placeholder bitmaps; other shells keep glyph fallbacks.
        /// </summary>
        private void ApplyFallbackVisual(bool group)
        {
            string imageKey = group ? "AvatarGroupFallbackImage" : "AvatarContactFallbackImage";
            ImageSource image = TryGetAppResource(imageKey) as ImageSource;
            if (image != null)
            {
                // The bitmap sits on the solid plate. Its own pixels are mostly
                // transparent, so using it as the only fill made the circle see-through.
                FallbackEllipse.Fill = new ImageBrush
                {
                    ImageSource = image,
                    Stretch = Stretch.UniformToFill
                };
                ContactFallbackIcon.Visibility = Visibility.Collapsed;
                GroupFallbackHost.Visibility = Visibility.Collapsed;
                return;
            }

            object brush = TryGetAppResource("AvatarFallbackBackgroundBrush");
            FallbackEllipse.Fill = brush as Brush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x45, 0x45, 0x45));
            ContactFallbackIcon.Visibility = group ? Visibility.Collapsed : Visibility.Visible;
            GroupFallbackHost.Visibility = group ? Visibility.Visible : Visibility.Collapsed;
        }

        private static object TryGetAppResource(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            try
            {
                if (Application.Current.Resources.ContainsKey(key))
                {
                    return Application.Current.Resources[key];
                }

                foreach (ResourceDictionary merged in Application.Current.Resources.MergedDictionaries)
                {
                    object found = TryGetFromDictionaryTree(merged, key);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static object TryGetFromDictionaryTree(ResourceDictionary dict, string key)
        {
            if (dict == null)
            {
                return null;
            }

            if (dict.ContainsKey(key))
            {
                return dict[key];
            }

            foreach (ResourceDictionary nested in dict.MergedDictionaries)
            {
                object found = TryGetFromDictionaryTree(nested, key);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
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
                // The control is handed a URL, not a chat, so the row has to be found by the
                // picture it is showing. All four candidates are checked because the header and
                // the list ask for different qualities of the same avatar.
                var chatState = App.Services?.GetService<IChatStateStore>();
                var contacts = App.Services?.GetService<IContactService>();
                if (chatState == null || contacts == null)
                {
                    return;
                }

                var chat = chatState.Chats
                    .FirstOrDefault(c =>
                        string.Equals(c.AvatarUrl, url, StringComparison.Ordinal) ||
                        string.Equals(c.AvatarHighUrl, url, StringComparison.Ordinal) ||
                        string.Equals(c.GetAvatarUrl(false), url, StringComparison.Ordinal) ||
                        string.Equals(c.GetAvatarUrl(true), url, StringComparison.Ordinal));
                if (chat != null)
                {
                    contacts.MarkAvatarImageLoadFailed(chat, "ui-brush-failed:" + (e?.ErrorMessage ?? "unknown"));
                }
            }
            catch
            {
            }
        }
    }
}
