using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Unison-shell message chrome: square corners and Path tails flush to the edge.
    /// Received: tip on first of run. Sent: tip on last of run (below).
    /// </summary>
    public sealed partial class UnisonMessageBubbleChrome : UserControl
    {
        public UnisonMessageBubbleChrome()
        {
            InitializeComponent();
            Loaded += (s, e) => UpdateVisual();
            UpdateVisual();
        }

        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(UnisonMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty ShowTailProperty =
            DependencyProperty.Register(
                nameof(ShowTail),
                typeof(bool),
                typeof(UnisonMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public static readonly DependencyProperty IsGroupChatProperty =
            DependencyProperty.Register(
                nameof(IsGroupChat),
                typeof(bool),
                typeof(UnisonMessageBubbleChrome),
                new PropertyMetadata(false, OnBubblePropertyChanged));

        public bool IsFromMe
        {
            get { return (bool)GetValue(IsFromMeProperty); }
            set { SetValue(IsFromMeProperty, value); }
        }

        /// <summary>True when this bubble should draw its tip (first received / last sent).</summary>
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
            var control = d as UnisonMessageBubbleChrome;
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
                // 1:1 and group sent: tip below on the last bubble.
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
