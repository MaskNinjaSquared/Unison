using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Icon + section title row shared by Settings and Debug surfaces.
    /// Parent sets <c>x:Uid</c> and an English <see cref="Text"/> fallback; MRT loads <c>{uid}.Text</c>.
    /// </summary>
    public sealed partial class SettingsSectionHeader : UserControl
    {
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(SettingsSectionHeader),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(SettingsSectionHeader),
                new PropertyMetadata(string.Empty));

        public SettingsSectionHeader()
        {
            this.InitializeComponent();
        }

        /// <summary>Segoe MDL2 glyph (e.g. E713).</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>Section heading; localized via parent <c>x:Uid</c> → <c>{uid}.Text</c>.</summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
    }
}
