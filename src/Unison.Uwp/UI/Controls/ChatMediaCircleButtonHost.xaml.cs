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
        private bool _legacyRing;
        private Ellipse _legacyRingEllipse;
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

            if (ShellUi.IsWhatsAppLegacy)
            {
                BuildLegacyRingButton();
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

        /// <summary>2px ring, transparent fill, the glyph. Same idea as the Legacy app-bar buttons.</summary>
        private void BuildLegacyRingButton()
        {
            _legacyRing = true;
            double diameter = UseOverlayChrome ? 56 : 36;
            FontFamily iconFont = LegacyMediaGlyphs.IconFont
                ?? (FontFamily)Application.Current.Resources["IconFont"];
            Brush ink = LegacyInkBrush();
            _whatsAppGlyph = new TextBlock
            {
                FontFamily = iconFont,
                FontSize = UseOverlayChrome ? 22 : 16,
                TextLineBounds = TextLineBounds.Tight,
                Foreground = ink,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _legacyRingEllipse = new Ellipse
            {
                Stroke = ink,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Colors.Transparent)
            };
            var content = new Grid { Width = diameter, Height = diameter };
            content.Children.Add(_legacyRingEllipse);
            content.Children.Add(_whatsAppGlyph);
            // MediaHitTargetButtonStyle: no rectangular PointerOver paint over the ring.
            _whatsAppButton = new Button
            {
                Style = TryStyle("MediaHitTargetButtonStyle")
                    ?? TryStyle("TransparentMediaButtonStyle")
                    ?? TryStyle("WhatsAppIconButtonStyle"),
                Width = diameter,
                Height = diameter,
                MinWidth = diameter,
                MinHeight = diameter,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = content
            };
            Root.Children.Add(_whatsAppButton);
        }

        private static Brush TryBrush(string key)
        {
            object brush;
            return Application.Current.Resources.TryGetValue(key, out brush) ? brush as Brush : null;
        }

        /// <summary>White on the dark Legacy page, black on the light one. Same brush as the app-bar ring.</summary>
        private static Brush LegacyInkBrush()
        {
            return TryBrush("LegacyAppBarRingBrush") ?? new SolidColorBrush(Colors.White);
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
                string glyph = Glyph ?? "\uE896";
                _whatsAppGlyph.Text = _legacyRing ? LegacyMediaGlyphs.Resolve(glyph) : glyph;
                if (_legacyRing)
                {
                    Brush ink = LegacyInkBrush();
                    _whatsAppGlyph.Foreground = ink;
                    if (_legacyRingEllipse != null)
                    {
                        _legacyRingEllipse.Stroke = ink;
                    }
                }
                else if (GlyphForeground != null)
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
