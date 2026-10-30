using System;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Composer <see cref="RichEditBox"/> bound to <see cref="BoundText"/> with WhatsApp
    /// Color Emoji on emoji graphemes. TextBox cannot mix FontFamily per character.
    /// </summary>
    /// <remarks>
    /// <see cref="ITextCharacterFormat.Name"/> only resolves installed fonts, so stamping the
    /// packaged face per range always fell back to the system Segoe UI Emoji. The control's
    /// FontFamily (<c>ComposerEmojiFont</c>) loads the packaged face, named "Segoe UI Emoji",
    /// first; it maps no ASCII, so body text falls through to MainFont.
    /// </remarks>
    public sealed class ChatComposerInput : RichEditBox
    {
        public static readonly DependencyProperty BoundTextProperty =
            DependencyProperty.Register(
                nameof(BoundText),
                typeof(string),
                typeof(ChatComposerInput),
                new PropertyMetadata(string.Empty, OnBoundTextChanged));

        private bool _syncing;
        private string _lastPlain = string.Empty;

        public ChatComposerInput()
        {
            AcceptsReturn = false;
            TextWrapping = TextWrapping.Wrap;
            BorderThickness = new Thickness(0);
            Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            Padding = new Thickness(10, 8, 6, 5);
            MinHeight = 40;
            MaxHeight = 192;
            VerticalAlignment = VerticalAlignment.Bottom;
            VerticalContentAlignment = VerticalAlignment.Center;
            IsColorFontEnabled = true;

            var font = Application.Current.Resources["ComposerEmojiFont"] as FontFamily;
            if (font != null)
            {
                FontFamily = font;
            }

            TextChanged += OnDocumentTextChanged;
        }

        /// <summary>Two-way plain text mirrored to the ViewModel composer.</summary>
        public string BoundText
        {
            get => (string)GetValue(BoundTextProperty) ?? string.Empty;
            set => SetValue(BoundTextProperty, value ?? string.Empty);
        }

        /// <summary>Caret index in plain-text coordinates (same as former TextBox.SelectionStart).</summary>
        public int CaretIndex
        {
            get => Document.Selection.StartPosition;
            set
            {
                int len = GetPlainText().Length;
                int caret = value < 0 ? 0 : (value > len ? len : value);
                Document.Selection.SetRange(caret, caret);
            }
        }

        /// <summary>
        /// True while BoundText owns the document. SelectionChanged must not chase
        /// <c>MentionInsertCaret</c> while SetText rewrites the document.
        /// </summary>
        public bool IsDocumentSyncing => _syncing;

        public string GetPlainText()
        {
            string text;
            Document.GetText(TextGetOptions.UseCrlf, out text);
            return TrimDocumentText(text);
        }

        public void FocusProgrammatic()
        {
            Focus(FocusState.Programmatic);
        }

        private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = d as ChatComposerInput;
            if (box == null || box._syncing)
            {
                return;
            }

            string next = e.NewValue as string ?? string.Empty;
            string current = box.GetPlainText();
            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return;
            }

            box._syncing = true;
            try
            {
                int caret = box.Document.Selection.StartPosition;
                box.Document.SetText(TextSetOptions.None, next);
                box._lastPlain = next;
                int len = next.Length;
                int restore = caret > len ? len : caret;
                box.Document.Selection.SetRange(restore, restore);
            }
            finally
            {
                box._syncing = false;
            }
        }

        private void OnDocumentTextChanged(object sender, RoutedEventArgs e)
        {
            if (_syncing)
            {
                return;
            }

            string plain = GetPlainText();

            // RichEdit raises TextChanged for format-only edits too.
            if (string.Equals(plain, _lastPlain, StringComparison.Ordinal))
            {
                return;
            }

            _lastPlain = plain;
            _syncing = true;
            try
            {
                if (!string.Equals(BoundText, plain, StringComparison.Ordinal))
                {
                    BoundText = plain;
                }
            }
            finally
            {
                _syncing = false;
            }
        }

        private static string TrimDocumentText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // RichEdit appends a trailing \r that is not part of the composer value.
            if (text.Length > 0 && text[text.Length - 1] == '\r')
            {
                text = text.Substring(0, text.Length - 1);
            }

            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
