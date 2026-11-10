using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Settings row box (Imgur SettingBox layout: header/body + control column).
    /// Localizes Header/Text from <see cref="LocalizationUid"/> via ResourceLoader on Loaded.
    /// Layout swaps via <c>SettingBoxLayoutTemplateSelector</c>: compact (toggle beside title)
    /// when <see cref="Text"/> is empty; expanded when there is a description.
    /// </summary>
    public sealed partial class SettingBox : UserControl
    {
        public SettingBox()
        {
            this.InitializeComponent();
            this.Loaded += SettingBox_Loaded;
        }

        private void SettingBox_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyUidResources();
            RefreshHasDescription();
        }

        /// <summary>
        /// Resource key prefix (e.g. Settings_Notifications) → `{key}/Header` and `{key}/Text`.
        /// </summary>
        public string LocalizationUid
        {
            get { return (string)GetValue(LocalizationUidProperty); }
            set { SetValue(LocalizationUidProperty, value); }
        }

        public static readonly DependencyProperty LocalizationUidProperty =
            DependencyProperty.Register(
                nameof(LocalizationUid),
                typeof(string),
                typeof(SettingBox),
                new PropertyMetadata(null));

        public void ApplyUidResources()
        {
            string key = LocalizationUid;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            try
            {
                ResourceLoader loader;
                try { loader = ResourceLoader.GetForCurrentView(); }
                catch { loader = ResourceLoader.GetForViewIndependentUse(); }

                string header = loader.GetString(key + "/Header");
                if (!string.IsNullOrEmpty(header))
                {
                    Header = header;
                }

                string text = loader.GetString(key + "/Text");
                if (!string.IsNullOrEmpty(text))
                {
                    Text = text;
                }
            }
            catch
            {
                // Keep designer / attribute fallbacks.
            }
        }

        /// <summary>Optional Segoe MDL2 glyph shown before the header.</summary>
        public string Glyph
        {
            get { return (string)GetValue(GlyphProperty); }
            set { SetValue(GlyphProperty, value); }
        }

        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingBox), new PropertyMetadata(null));

        public string Header
        {
            get { return (string)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingBox), new PropertyMetadata(null));

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(SettingBox),
                new PropertyMetadata(null, OnTextChanged));

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SettingBox)d).RefreshHasDescription();
        }

        /// <summary>True when <see cref="Text"/> has content — drives the layout template.</summary>
        public bool HasDescription
        {
            get { return (bool)GetValue(HasDescriptionProperty); }
            private set { SetValue(HasDescriptionProperty, value); }
        }

        public static readonly DependencyProperty HasDescriptionProperty =
            DependencyProperty.Register(
                nameof(HasDescription),
                typeof(bool),
                typeof(SettingBox),
                new PropertyMetadata(false));

        private void RefreshHasDescription()
        {
            HasDescription = !string.IsNullOrWhiteSpace(Text);
        }

        public object Control
        {
            get { return GetValue(ControlProperty); }
            set { SetValue(ControlProperty, value); }
        }

        public static readonly DependencyProperty ControlProperty =
            DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingBox), new PropertyMetadata(null));
    }
}
