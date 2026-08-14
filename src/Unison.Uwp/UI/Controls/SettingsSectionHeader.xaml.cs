using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Icon + section title row shared by Settings and Debug surfaces.
    /// Optional <see cref="LocalizationUid"/> loads <c>{uid}.Text</c> into <see cref="Title"/>.
    /// </summary>
    public sealed partial class SettingsSectionHeader : UserControl
    {
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(
                nameof(Glyph),
                typeof(string),
                typeof(SettingsSectionHeader),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(SettingsSectionHeader),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty LocalizationUidProperty =
            DependencyProperty.Register(
                nameof(LocalizationUid),
                typeof(string),
                typeof(SettingsSectionHeader),
                new PropertyMetadata(null));

        public SettingsSectionHeader()
        {
            this.InitializeComponent();
            this.Loaded += SettingsSectionHeader_Loaded;
        }

        /// <summary>Segoe MDL2 glyph (e.g. E713).</summary>
        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        /// <summary>Section heading fallback text when localization is missing.</summary>
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>Resource key prefix; loads <c>{LocalizationUid}.Text</c>.</summary>
        public string LocalizationUid
        {
            get => (string)GetValue(LocalizationUidProperty);
            set => SetValue(LocalizationUidProperty, value);
        }

        private void SettingsSectionHeader_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LocalizationUid))
            {
                return;
            }

            try
            {
                var loader = ResourceLoader.GetForCurrentView();
                string text = loader.GetString(LocalizationUid + "/Text");
                if (!string.IsNullOrEmpty(text))
                {
                    Title = text;
                }
            }
            catch
            {
            }
        }
    }
}
