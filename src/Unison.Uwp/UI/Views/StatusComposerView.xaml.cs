using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>Full-screen text status composer (Unison / WhatsApp shells).</summary>
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
    }
}
