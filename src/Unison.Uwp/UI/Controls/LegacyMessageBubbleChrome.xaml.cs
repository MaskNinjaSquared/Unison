using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// WhatsApp Legacy bubble chrome. 1:1 tails sit 10px in from the outer edge.
    /// In a group the received tail is flush left, pointing at the sender photo, same place as Unison.
    /// </summary>
    public sealed partial class LegacyMessageBubbleChrome : UserControl
    {
        public LegacyMessageBubbleChrome()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateVisual();
            UpdateVisual();
        }

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(LegacyMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty ShowTailProperty =
            DependencyProperty.Register(
                nameof(ShowTail),
                typeof(bool),
                typeof(LegacyMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsGroupChatProperty =
            DependencyProperty.Register(
                nameof(IsGroupChat),
                typeof(bool),
                typeof(LegacyMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public bool IsFromMe
        {
            get { return (bool)GetValue(IsFromMeProperty); }
            set { SetValue(IsFromMeProperty, value); }
        }

        public bool ShowTail
        {
            get { return (bool)GetValue(ShowTailProperty); }
            set { SetValue(ShowTailProperty, value); }
        }

        public bool IsGroupChat
        {
            get { return (bool)GetValue(IsGroupChatProperty); }
            set { SetValue(IsGroupChatProperty, value); }
        }

        private static void OnBubblePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LegacyMessageBubbleChrome;
            if (control != null)
            {
                control.UpdateVisual();
            }
        }

        private void UpdateVisual()
        {
            bool isFromMe = IsFromMe;
            bool showTail = ShowTail;
            bool isGroup = IsGroupChat;

            OutgoingHost.Visibility = isFromMe ? Visibility.Visible : Visibility.Collapsed;
            IncomingHost.Visibility = isFromMe ? Visibility.Collapsed : Visibility.Visible;

            OutgoingTailBelow.Visibility = Visibility.Collapsed;
            IncomingTailUp.Visibility = Visibility.Collapsed;
            IncomingTailSide.Visibility = Visibility.Collapsed;

            if (!showTail)
            {
                return;
            }

            if (isFromMe)
            {
                OutgoingTailBelow.Visibility = Visibility.Visible;
            }
            else if (isGroup)
            {
                IncomingTailSide.Visibility = Visibility.Visible;
            }
            else
            {
                IncomingTailUp.Visibility = Visibility.Visible;
            }
        }
    }
}
