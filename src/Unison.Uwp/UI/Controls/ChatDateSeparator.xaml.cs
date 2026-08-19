using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Centered date chip above a timeline bubble (Hoje / Ontem / short date).
    /// Fill is <c>ChatDetailDateSeparatorBackgroundBrush</c> (wallpaper accent).
    /// </summary>
    public sealed partial class ChatDateSeparator : UserControl
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(ChatDateSeparator),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public ChatDateSeparator()
        {
            InitializeComponent();
        }

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as ChatDateSeparator;
            if (control?.LabelText == null)
            {
                return;
            }

            control.LabelText.Text = e.NewValue as string ?? string.Empty;
        }
    }
}
