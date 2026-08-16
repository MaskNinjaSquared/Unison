using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class DebugView : Page
    {
        private bool _isActive;
        private DebugViewModel _viewModel;
        private readonly DispatcherTimer _logFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        private readonly DispatcherTimer _runtimeRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };

        private DebugViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<DebugViewModel>();
                    DataContext = _viewModel;
                }

                return _viewModel;
            }
        }

        public DebugView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            StripToggleLabels(VerboseLoggingToggle);
            StripToggleLabels(SessionLoggingToggle);
            _logFlushTimer.Tick += (s, e) => ViewModel?.FlushPendingLogLines();
            _runtimeRefreshTimer.Tick += (s, e) => ViewModel?.RefreshRuntimeHealth();
            Loaded += DebugView_Loaded;
            Unloaded += DebugView_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Activate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            Deactivate();
        }

        private void DebugView_Loaded(object sender, RoutedEventArgs e)
        {
            if (Visibility == Visibility.Visible)
            {
                Activate();
            }
        }

        private void DebugView_Unloaded(object sender, RoutedEventArgs e)
        {
            Deactivate();
        }

        private void Activate()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            string buildInfo;
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                buildInfo = string.Format(
                    "Build: {0}.{1}.{2}.{3} ({4})",
                    version.Major,
                    version.Minor,
                    version.Build,
                    version.Revision,
                    Package.Current.Id.Architecture);
            }
            catch
            {
                buildInfo = "Build: ?";
            }

            DebugViewModel vm = ViewModel;
            if (vm != null)
            {
                vm.BackRequested -= ViewModel_BackRequested;
                vm.BackRequested += ViewModel_BackRequested;
                vm.LogTextChanged -= ViewModel_LogTextChanged;
                vm.LogTextChanged += ViewModel_LogTextChanged;
                vm.Activate(buildInfo);
            }

            StripToggleLabels(VerboseLoggingToggle);
            StripToggleLabels(SessionLoggingToggle);
            _logFlushTimer.Start();
            _runtimeRefreshTimer.Start();
        }

        private void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            if (_viewModel != null)
            {
                _viewModel.BackRequested -= ViewModel_BackRequested;
                _viewModel.LogTextChanged -= ViewModel_LogTextChanged;
                _viewModel.Deactivate();
            }

            _logFlushTimer.Stop();
            _runtimeRefreshTimer.Stop();
        }

        private void ViewModel_BackRequested(object sender, EventArgs e)
        {
            App.Services?.GetService<ShellViewModel>()?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
        }

        private void ViewModel_LogTextChanged(object sender, EventArgs e)
        {
            try
            {
                SessionLogScroller?.ChangeView(null, SessionLogScroller.ScrollableHeight, null, true);
            }
            catch
            {
            }
        }

        /// <summary>
        /// UWP ToggleSwitch still reserves On/Off label width for "". Use collapsed
        /// zero-size content so the knob sits flush right like Settings.
        /// </summary>
        private static void StripToggleLabels(ToggleSwitch toggle)
        {
            if (toggle == null)
            {
                return;
            }

            toggle.ClearValue(FrameworkElement.WidthProperty);
            toggle.MinWidth = 0;
            toggle.Margin = new Thickness(0);
            toggle.OnContent = CreateEmptyToggleContent();
            toggle.OffContent = CreateEmptyToggleContent();
        }

        private static UIElement CreateEmptyToggleContent()
        {
            return new Border
            {
                Width = 0,
                Height = 0,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
        }
    }
}
