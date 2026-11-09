// =============================================================================
// RichLabeledTextBox
//
// Labeled field: header inside TextBox chrome + ChatComposerInput + emoji on the input row.
// Hosts that own an emoji dock subscribe to EmojiInvoked.
// =============================================================================
using System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    public sealed partial class RichLabeledTextBox : UserControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(string),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(
                nameof(PlaceholderText),
                typeof(string),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty AcceptsReturnProperty =
            DependencyProperty.Register(
                nameof(AcceptsReturn),
                typeof(bool),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(false));

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(
                nameof(TextWrapping),
                typeof(TextWrapping),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(TextWrapping.NoWrap));

        public static readonly DependencyProperty InputVerticalContentAlignmentProperty =
            DependencyProperty.Register(
                nameof(InputVerticalContentAlignment),
                typeof(VerticalAlignment),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(VerticalAlignment.Center));

        public static readonly DependencyProperty MinBoxHeightProperty =
            DependencyProperty.Register(
                nameof(MinBoxHeight),
                typeof(double),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(56d));

        public static readonly DependencyProperty MaxInputHeightProperty =
            DependencyProperty.Register(
                nameof(MaxInputHeight),
                typeof(double),
                typeof(RichLabeledTextBox),
                new PropertyMetadata(192d));

        public RichLabeledTextBox()
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

        /// <summary>Inserts <paramref name="emoji"/> at the caret (emoji-dock hosts).</summary>
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

            // Drop composer focus before SetText — Mobile reopens SIP if the box stays focused.
            DismissSoftKeyboard(EmojiButton);

            string next = current.Insert(caret, emoji);
            Text = next;
            InputBox.BoundText = next;
            InputBox.CaretIndex = caret + emoji.Length;
        }

        public void FocusInput()
        {
            InputBox?.FocusProgrammatic();
        }

        /// <summary>Drops composer focus and hides the Mobile SIP (emoji dock hosts).</summary>
        public void DismissSoftKeyboard()
        {
            DismissSoftKeyboard(EmojiButton);
        }

        private void EmojiButton_Click(object sender, RoutedEventArgs e)
        {
            DismissSoftKeyboard(sender as Control);
            EmojiInvoked?.Invoke(this, EventArgs.Empty);
        }

        private static void DismissSoftKeyboard(Control focusTarget)
        {
            // TryHide alone fails while a RichEditBox still has focus on Mobile.
            focusTarget?.Focus(FocusState.Programmatic);
            try
            {
                InputPane.GetForCurrentView()?.TryHide();
            }
            catch
            {
                // View may be tearing down.
            }
        }
    }
}
