using System.Windows.Input;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Media action host: Unison brand circle vs WhatsApp icon (optional dark ellipse overlay).
    /// Used for audio, image, video, and document download/play affordances.
    /// </summary>
    public sealed partial class ChatMediaCircleButtonHost : UserControl
    {
        private bool _built;
        private UnisonMediaCircleButton _unisonButton;
        private Button _whatsAppButton;
        private TextBlock _whatsAppGlyph;

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(ChatMediaCircleButtonHost),
                new PropertyMetadata(false, OnHostPropertyChanged));

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(ChatMediaCircleButtonHost),
                new PropertyMetadata("\uE896", OnHostPropertyChanged));

        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(ChatMediaCircleButtonHost),
                new PropertyMetadata(null, OnHostPropertyChanged));

        public static readonly DependencyProperty GlyphForegroundProperty =
            DependencyProperty.Register(
                nameof(GlyphForeground),
                typeof(Brush),
                typeof(ChatMediaCircleButtonHost),
                new PropertyMetadata(null, OnHostPropertyChanged));

        public static readonly DependencyProperty UseOverlayChromeProperty =
            DependencyProperty.Register(
                nameof(UseOverlayChrome),
                typeof(bool),
                typeof(ChatMediaCircleButtonHost),
                new PropertyMetadata(false, OnHostPropertyChanged));

        public ChatMediaCircleButtonHost()
        {
            InitializeComponent();
            Loaded += ChatMediaCircleButtonHost_Loaded;
        }

        public bool IsFromMe
        {
            get => (bool)GetValue(IsFromMeProperty);
            set => SetValue(IsFromMeProperty, value);
        }

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public ICommand Command
        {
            get => (ICommand)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        /// <summary>WhatsApp glyph color (compact / overlay).</summary>
        public Brush GlyphForeground
        {
            get => (Brush)GetValue(GlyphForegroundProperty);
            set => SetValue(GlyphForegroundProperty, value);
        }

        /// <summary>
        /// Image/video overlays: 56×56 chrome (matches play). Audio/doc stay compact 30×30 / 36.
        /// </summary>
        public bool UseOverlayChrome
        {
            get => (bool)GetValue(UseOverlayChromeProperty);
            set => SetValue(UseOverlayChromeProperty, value);
        }

        private static void OnHostPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = d as ChatMediaCircleButtonHost;
            host?.SyncChildren();
        }

        private void ChatMediaCircleButtonHost_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureButton();
            SyncChildren();
        }

        private void EnsureButton()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            Root.Children.Clear();

            if (ShellUi.IsUnison)
            {
                _unisonButton = new UnisonMediaCircleButton
                {
                    Diameter = UseOverlayChrome
                        ? UnisonMediaCircleButton.OverlayDiameter
                        : UnisonMediaCircleButton.CompactDiameter
                };
                Root.Children.Add(_unisonButton);
                return;
            }

            FontFamily iconFont = (FontFamily)Application.Current.Resources["IconFont"];
            if (UseOverlayChrome)
            {
                _whatsAppGlyph = new TextBlock
                {
                    FontFamily = iconFont,
                    FontSize = 28,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var content = new Grid { Width = 56, Height = 56 };
                content.Children.Add(new Ellipse
                {
                    Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0))
                });
                content.Children.Add(_whatsAppGlyph);

                _whatsAppButton = new Button
                {
                    Style = TryStyle("TransparentMediaButtonStyle"),
                    Width = 56,
                    Height = 56,
                    MinWidth = 56,
                    MinHeight = 56,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = content
                };
            }
            else
            {
                _whatsAppGlyph = new TextBlock
                {
                    FontFamily = iconFont,
                    FontSize = 20
                };
                _whatsAppButton = new Button
                {
                    Style = TryStyle("WhatsAppIconButtonStyle"),
                    Padding = new Thickness(0),
                    MinWidth = 36,
                    MinHeight = 36,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Content = _whatsAppGlyph
                };
            }

            Root.Children.Add(_whatsAppButton);
        }

        private void SyncChildren()
        {
            if (_unisonButton != null)
            {
                _unisonButton.IsFromMe = IsFromMe;
                _unisonButton.Glyph = Glyph;
                _unisonButton.Command = Command;
                _unisonButton.Diameter = UseOverlayChrome
                    ? UnisonMediaCircleButton.OverlayDiameter
                    : UnisonMediaCircleButton.CompactDiameter;
            }

            if (_whatsAppButton != null)
            {
                _whatsAppGlyph.Text = Glyph ?? "\uE896";
                if (GlyphForeground != null)
                {
                    _whatsAppGlyph.Foreground = GlyphForeground;
                }

                _whatsAppButton.Command = Command;
            }
        }

        private static Style TryStyle(string key)
        {
            object styleObj;
            return Application.Current.Resources.TryGetValue(key, out styleObj)
                ? styleObj as Style
                : null;
        }
    }
}
