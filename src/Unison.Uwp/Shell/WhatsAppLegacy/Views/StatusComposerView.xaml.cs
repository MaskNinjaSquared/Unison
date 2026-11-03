using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.WhatsAppLegacy.Views
{
    /// <summary>Full-screen text status composer (Legacy shell, bottom CommandBar).</summary>
    public sealed partial class StatusComposerView : Page, IFullScreenShellPage
    {
        public StatusComposerView()
        {
            if (App.Services != null)
            {
                ViewModel = App.Services.GetRequiredService<StatusComposerViewModel>();
                DataContext = ViewModel;
            }

            InitializeComponent();
        }

        public StatusComposerViewModel ViewModel { get; }

        public bool TryHandleBack() => ViewModel != null && ViewModel.TryHandleBack();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel?.Reset();
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => StatusTextInput.Focus(FocusState.Programmatic));
        }

        private void ComposerCommandBar_Loaded(object sender, RoutedEventArgs e)
        {
            LegacyCommandBarLayout.CenterPrimaryCommands(ComposerCommandBar);
        }
    }
}
