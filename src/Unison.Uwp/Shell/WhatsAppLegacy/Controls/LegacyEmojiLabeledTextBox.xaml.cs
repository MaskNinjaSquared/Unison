// =============================================================================
// LegacyEmojiLabeledTextBox
//
// WP8 search-chrome field: header above, emoji inside. Same public surface as
// RichLabeledTextBox so create-flow hosts can swap per shell without hacks.
// =============================================================================
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Controls
{
    public sealed partial class LegacyEmojiLabeledTextBox : UserControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(string),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(
                nameof(PlaceholderText),
                typeof(string),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty AcceptsReturnProperty =
            DependencyProperty.Register(
                nameof(AcceptsReturn),
                typeof(bool),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(false));

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(
                nameof(TextWrapping),
                typeof(TextWrapping),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(TextWrapping.NoWrap));

        public static readonly DependencyProperty InputVerticalContentAlignmentProperty =
            DependencyProperty.Register(
                nameof(InputVerticalContentAlignment),
                typeof(VerticalAlignment),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(VerticalAlignment.Center));

        public static readonly DependencyProperty MinBoxHeightProperty =
            DependencyProperty.Register(
                nameof(MinBoxHeight),
                typeof(double),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(36d));

        public static readonly DependencyProperty MaxInputHeightProperty =
            DependencyProperty.Register(
                nameof(MaxInputHeight),
                typeof(double),
                typeof(LegacyEmojiLabeledTextBox),
                new PropertyMetadata(192d));

        public LegacyEmojiLabeledTextBox()
        {
            InitializeComponent();
        }

        /// <summary>Raised when the emoji glyph is tapped; host toggles its emoji dock.</summary>
        public event EventHandler EmojiInvoked;

        public string Header
        {
            get => (string)GetValue(HeaderProperty) ?? string.Empty;
            set => SetValue(HeaderProperty, value ?? string.Empty);
        }

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty) ?? string.Empty;
            set => SetValue(PlaceholderTextProperty, value ?? string.Empty);
        }

        public string Text
        {
            get => (string)GetValue(TextProperty) ?? string.Empty;
            set => SetValue(TextProperty, value ?? string.Empty);
        }

        public bool AcceptsReturn
        {
            get => (bool)GetValue(AcceptsReturnProperty);
            set => SetValue(AcceptsReturnProperty, value);
        }

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)GetValue(TextWrappingProperty);
            set => SetValue(TextWrappingProperty, value);
        }

        public VerticalAlignment InputVerticalContentAlignment
        {
            get => (VerticalAlignment)GetValue(InputVerticalContentAlignmentProperty);
            set => SetValue(InputVerticalContentAlignmentProperty, value);
        }

        public double MinBoxHeight
        {
            get => (double)GetValue(MinBoxHeightProperty);
            set => SetValue(MinBoxHeightProperty, value);
        }

        public double MaxInputHeight
        {
            get => (double)GetValue(MaxInputHeightProperty);
            set => SetValue(MaxInputHeightProperty, value);
        }

        public void InsertEmoji(string emoji)
        {
            if (string.IsNullOrEmpty(emoji) || InputBox == null)
            {
                return;
            }

            string current = InputBox.GetPlainText() ?? string.Empty;
            int caret = InputBox.CaretIndex;
            if (caret < 0)
            {
                caret = current.Length;
            }

            if (caret > current.Length)
            {
                caret = current.Length;
            }

            string next = current.Insert(caret, emoji);
            Text = next;
            InputBox.BoundText = next;
            InputBox.CaretIndex = caret + emoji.Length;
            InputBox.FocusProgrammatic();
        }

        public void FocusInput()
        {
            InputBox?.FocusProgrammatic();
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            EmojiInvoked?.Invoke(this, EventArgs.Empty);
        }
    }
}
