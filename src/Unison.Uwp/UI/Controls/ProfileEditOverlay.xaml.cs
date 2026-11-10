using System.Windows.Input;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Full-bleed Name/About editor over the own-profile settings surface.
    /// Save is local-only (no websocket) until the profile bridge exposes About/name IQ.
    /// </summary>
    public sealed partial class ProfileEditOverlay : UserControl
    {
        public ProfileEditOverlay()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty IsLegacyChromeProperty =
            DependencyProperty.Register(
                nameof(IsLegacyChrome),
                typeof(bool),
                typeof(ProfileEditOverlay),
                new PropertyMetadata(false));

        /// <summary>True shows Legacy CommandBar; false shows the round FAB.</summary>
        public bool IsLegacyChrome
        {
            get => (bool)GetValue(IsLegacyChromeProperty);
            set => SetValue(IsLegacyChromeProperty, value);
        }

        private void ProfileEmojiPanel_GlyphPicked(object sender, string glyph)
        {
            var vm = DataContext as SettingsViewModel;
            if (vm?.InsertProfileEmojiCommand is ICommand command && command.CanExecute(glyph))
            {
                command.Execute(glyph);
            }
        }
    }
}
