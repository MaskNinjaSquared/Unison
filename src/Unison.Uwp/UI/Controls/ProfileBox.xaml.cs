using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Profile row button: icon, title, and value. Parent sets <c>x:Uid</c> so MRT loads <c>{uid}.Title</c>.
    /// An empty <see cref="Value"/> shows an em dash.
    /// </summary>
    public sealed partial class ProfileBox : UserControl
    {
        public event EventHandler Click;

        public ProfileBox()
        {
            InitializeComponent();
            Loaded += ProfileBox_Loaded;
        }

        private void ProfileBox_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyValue();
        }

        /// <summary>Segoe MDL2 glyph.</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(ProfileBox),
                new PropertyMetadata(string.Empty));

        /// <summary>Row title. Localized via parent <c>x:Uid</c> → <c>{uid}.Title</c>.</summary>
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(ProfileBox),
                new PropertyMetadata(string.Empty));

        /// <summary>Row value. Whitespace shows an em dash.</summary>
        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(string),
                typeof(ProfileBox),
                new PropertyMetadata(string.Empty, OnValueChanged));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ProfileBox)d).ApplyValue();
        }

        private void ApplyValue()
        {
            if (ValueText == null)
            {
                return;
            }

            string text = Value;
            ValueText.Text = string.IsNullOrWhiteSpace(text) ? "\u2014" : text;
        }

        private void RootButton_Click(object sender, RoutedEventArgs e)
        {
            Click?.Invoke(this, EventArgs.Empty);
        }
    }
}
