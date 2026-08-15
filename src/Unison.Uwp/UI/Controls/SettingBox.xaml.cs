using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Settings row box (Imgur SettingBox layout: header/body + control column).
    /// Localizes Header/Text from <see cref="LocalizationUid"/> via ResourceLoader on Loaded.
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
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(SettingBox), new PropertyMetadata(null));

        public object Control
        {
            get { return GetValue(ControlProperty); }
            set { SetValue(ControlProperty, value); }
        }

        public static readonly DependencyProperty ControlProperty =
            DependencyProperty.Register(nameof(Control), typeof(object), typeof(SettingBox), new PropertyMetadata(null));
    }
}
