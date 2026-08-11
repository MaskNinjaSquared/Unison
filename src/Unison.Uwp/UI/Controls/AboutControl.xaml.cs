using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// About + Developers block (Imgur AboutControl layout).
    /// Expects <see cref="SettingsViewModel"/> as DataContext; binds with x:Bind.
    /// </summary>
    public sealed partial class AboutControl : UserControl
    {
        public AboutControl()
        {
            this.InitializeComponent();
            DataContextChanged += AboutControl_DataContextChanged;
        }

        /// <summary>Typed ViewModel for compiled bindings (updated on DataContextChanged).</summary>
        public SettingsViewModel ViewModel { get; private set; }

        private void AboutControl_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            ViewModel = DataContext as SettingsViewModel;
            Bindings.Update();
        }
    }
}
