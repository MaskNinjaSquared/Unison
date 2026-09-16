using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Template-facing bubble chrome: Unison square+Path vs WhatsApp rounded masks.
    /// Unison: received tip on first of run; sent tip on last. WhatsApp: tip on first.
    /// </summary>
    public sealed partial class MessageBubbleChromeHost : UserControl
    {
        private bool _shellResolved;
        private bool _useUnison;

        public MessageBubbleChromeHost()
        {
            InitializeComponent();
            Loaded += MessageBubbleChromeHost_Loaded;
        }

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(MessageBubbleChromeHost),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsRunStartProperty =
            DependencyProperty.Register(
                nameof(IsRunStart),
                typeof(bool),
                typeof(MessageBubbleChromeHost),
                new PropertyMetadata(true, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsRunEndProperty =
            DependencyProperty.Register(
                nameof(IsRunEnd),
                typeof(bool),
                typeof(MessageBubbleChromeHost),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsGroupChatProperty =
            DependencyProperty.Register(
                nameof(IsGroupChat),
                typeof(bool),
                typeof(MessageBubbleChromeHost),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public bool IsFromMe
        {
            get { return (bool)GetValue(IsFromMeProperty); }
            set { SetValue(IsFromMeProperty, value); }
        }

        public bool IsRunStart
        {
            get { return (bool)GetValue(IsRunStartProperty); }
            set { SetValue(IsRunStartProperty, value); }
        }

        public bool IsRunEnd
        {
            get { return (bool)GetValue(IsRunEndProperty); }
            set { SetValue(IsRunEndProperty, value); }
        }

        public bool IsGroupChat
        {
            get { return (bool)GetValue(IsGroupChatProperty); }
            set { SetValue(IsGroupChatProperty, value); }
        }

        private static void OnBubblePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = d as MessageBubbleChromeHost;
            if (host != null)
            {
                host.SyncChildren();
            }
        }

        private void MessageBubbleChromeHost_Loaded(object sender, RoutedEventArgs e)
        {
            ResolveShell();
            ApplyShellVisibility();
            SyncChildren();
        }

        private void ResolveShell()
        {
            if (_shellResolved)
            {
                return;
            }

            _useUnison = ShellUi.IsUnison;
            _shellResolved = true;
        }

        private void ApplyShellVisibility()
        {
            if (UnisonChrome != null)
            {
                UnisonChrome.Visibility = _useUnison ? Visibility.Visible : Visibility.Collapsed;
            }

            if (WhatsAppChrome != null)
            {
                WhatsAppChrome.Visibility = _useUnison ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SyncChildren()
        {
            bool fromMe = IsFromMe;
            bool runStart = IsRunStart;
            bool runEnd = IsRunEnd;
            bool isGroup = IsGroupChat;

            if (UnisonChrome != null)
            {
                UnisonChrome.IsFromMe = fromMe;
                // Received: first of run (top tips). Sent: last of run (bottom tip).
                UnisonChrome.ShowTail = fromMe ? runEnd : runStart;
                UnisonChrome.IsGroupChat = isGroup;
            }

            if (WhatsAppChrome != null)
            {
                WhatsAppChrome.IsFromMe = fromMe;
                WhatsAppChrome.IsRunStart = runStart;
            }
        }
    }
}
