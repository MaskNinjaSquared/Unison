using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Template-facing voice transport: creates Unison or WhatsApp bar for the active AppShell.
    /// </summary>
    public sealed partial class ChatAudioBubbleBarHost : UserControl
    {
        private bool _built;
        private UnisonChatAudioBubbleBar _unisonBar;
        private ChatAudioBubbleBar _whatsAppBar;

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(ChatAudioBubbleBarHost),
                new PropertyMetadata(false, OnHostPropertyChanged));

        public static readonly DependencyProperty GlyphForegroundProperty =
            DependencyProperty.Register(
                nameof(GlyphForeground),
                typeof(Brush),
                typeof(ChatAudioBubbleBarHost),
                new PropertyMetadata(null, OnHostPropertyChanged));

        public ChatAudioBubbleBarHost()
        {
            InitializeComponent();
            Loaded += ChatAudioBubbleBarHost_Loaded;
        }

        public bool IsFromMe
        {
            get => (bool)GetValue(IsFromMeProperty);
            set => SetValue(IsFromMeProperty, value);
        }

        /// <summary>WhatsApp icon/label foreground only.</summary>
        public Brush GlyphForeground
        {
            get => (Brush)GetValue(GlyphForegroundProperty);
            set => SetValue(GlyphForegroundProperty, value);
        }

        private static void OnHostPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = d as ChatAudioBubbleBarHost;
            host?.SyncChildren();
        }

        private void ChatAudioBubbleBarHost_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureBar();
            SyncChildren();
        }

        private void EnsureBar()
        {
            if (_built)
            {
                return;
            }

            _built = true;
            Root.Children.Clear();

            if (ShellUi.IsUnison)
            {
                _unisonBar = new UnisonChatAudioBubbleBar
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Root.Children.Add(_unisonBar);
            }
            else
            {
                _whatsAppBar = new ChatAudioBubbleBar
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Root.Children.Add(_whatsAppBar);
            }
        }

        private void SyncChildren()
        {
            if (_unisonBar != null)
            {
                _unisonBar.IsFromMe = IsFromMe;
            }

            if (_whatsAppBar != null)
            {
                _whatsAppBar.GlyphForeground = GlyphForeground;
            }
        }
    }
}
