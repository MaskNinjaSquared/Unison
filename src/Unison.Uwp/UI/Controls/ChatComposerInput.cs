using System;
using Unison.Core.Helpers;
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
    /// RichEdit routes many emoji codepoints through the system color-emoji path (Segoe UI
    /// Emoji) even after <see cref="ITextCharacterFormat.Name"/> is set. We still apply
    /// WhatsApp Color Emoji on every change and re-apply once on the dispatcher so picker
    /// inserts and typed glyphs share the same pass. Message bubbles use
    /// <c>EmojiRichRuns</c> (TextBlock Runs), which honor the packaged font reliably.
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
        private string _emojiFontName;
        private string _bodyFontName;
        private bool _emojiPassQueued;

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
            TextChanged += OnDocumentTextChanged;
            Loaded += OnLoaded;
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Force DWrite to load the packaged CBDT face before the first CharacterFormat.Name set.
            try
            {
                FontFamily emoji = Application.Current.Resources["EmojiFont"] as FontFamily;
                if (emoji != null)
                {
                    var probe = new TextBlock
                    {
                        FontFamily = emoji,
                        Text = "\U0001F600",
                        IsColorFontEnabled = true,
                        FontSize = 1,
                        Opacity = 0,
                        IsHitTestVisible = false
                    };
                    probe.Measure(new Windows.Foundation.Size(1, 1));
                }
            }
            catch
            {
                // Probe is best-effort; ApplyEmojiFonts still runs.
            }

            string body = ResolveBodyFontName();
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    ITextCharacterFormat defaults = Document.GetDefaultCharacterFormat();
                    defaults.Name = body;
                    Document.SetDefaultCharacterFormat(defaults);
                }
                catch
                {
                }
            }
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
                int len = next.Length;
                int restore = caret > len ? len : caret;
                box.Document.Selection.SetRange(restore, restore);
                box.ApplyEmojiFonts(next);
                box.QueueEmojiFontPass();
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
            _syncing = true;
            try
            {
                if (!string.Equals(BoundText, plain, StringComparison.Ordinal))
                {
                    BoundText = plain;
                }

                ApplyEmojiFonts(plain);
                QueueEmojiFontPass();
            }
            finally
            {
                _syncing = false;
            }
        }

        /// <summary>
        /// RichEdit often reapplies Segoe UI Emoji after TextChanged returns; one Normal
        /// dispatcher turn re-stamps WhatsApp Color Emoji once the document has settled.
        /// </summary>
        private void QueueEmojiFontPass()
        {
            if (_emojiPassQueued || !Dispatcher.HasThreadAccess)
            {
                return;
            }

            _emojiPassQueued = true;
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                _emojiPassQueued = false;
                if (_syncing)
                {
                    return;
                }

                ApplyEmojiFonts(GetPlainText());
            });
        }

        private void ApplyEmojiFonts(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return;
            }

            string emojiName = ResolveEmojiFontName();
            if (string.IsNullOrEmpty(emojiName))
            {
                return;
            }

            string bodyName = ResolveBodyFontName();

            // Ranges must use the document's own length model (trailing \r), not TrimDocumentText.
            string documentText;
            Document.GetText(TextGetOptions.UseCrlf, out documentText);
            if (string.IsNullOrEmpty(documentText))
            {
                return;
            }

            // Drop RichEdit's trailing paragraph mark for splitting only.
            string splitSource = documentText;
            if (splitSource.Length > 0 && splitSource[splitSource.Length - 1] == '\r')
            {
                splitSource = splitSource.Substring(0, splitSource.Length - 1);
            }

            int caret = Document.Selection.StartPosition;
            int end = Document.Selection.EndPosition;
            var segments = EmojiText.Split(splitSource);
            int offset = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                EmojiText.Segment segment = segments[i];
                if (string.IsNullOrEmpty(segment.Text))
                {
                    continue;
                }

                int length = segment.Text.Length;
                Document.Selection.SetRange(offset, offset + length);
                ITextCharacterFormat format = Document.Selection.CharacterFormat;
                format.Name = segment.IsEmoji
                    ? emojiName
                    : (bodyName ?? format.Name);
                offset += length;
            }

            int docLen = splitSource.Length;
            int restoreEnd = end <= docLen ? end : caret;
            int restoreStart = caret <= docLen ? caret : docLen;
            Document.Selection.SetRange(restoreStart, restoreEnd);
        }

        private string ResolveBodyFontName()
        {
            if (_bodyFontName != null)
            {
                return _bodyFontName;
            }

            try
            {
                var main = Application.Current.Resources["MainFont"] as FontFamily;
                string bodyName = main?.Source;
                if (!string.IsNullOrEmpty(bodyName))
                {
                    int hash = bodyName.LastIndexOf('#');
                    if (hash >= 0 && hash + 1 < bodyName.Length)
                    {
                        bodyName = bodyName.Substring(hash + 1);
                    }

                    int comma = bodyName.IndexOf(',');
                    if (comma > 0)
                    {
                        bodyName = bodyName.Substring(0, comma).Trim();
                    }
                }

                _bodyFontName = bodyName;
            }
            catch
            {
                _bodyFontName = null;
            }

            return _bodyFontName;
        }

        private string ResolveEmojiFontName()
        {
            if (_emojiFontName != null)
            {
                return _emojiFontName;
            }

            try
            {
                var family = Application.Current.Resources["EmojiFont"] as FontFamily;
                string source = family?.Source;
                if (!string.IsNullOrEmpty(source))
                {
                    int hash = source.LastIndexOf('#');
                    _emojiFontName = hash >= 0 && hash + 1 < source.Length
                        ? source.Substring(hash + 1)
                        : "WhatsApp Color Emoji";
                    // Family list may include ", Segoe UI Emoji" — take first face only.
                    // Using the OS fallback name here is exactly why typing showed Segoe glyphs.
                    int comma = _emojiFontName.IndexOf(',');
                    if (comma > 0)
                    {
                        _emojiFontName = _emojiFontName.Substring(0, comma).Trim();
                    }
                }
                else
                {
                    _emojiFontName = "WhatsApp Color Emoji";
                }
            }
            catch
            {
                _emojiFontName = "WhatsApp Color Emoji";
            }

            return _emojiFontName;
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
