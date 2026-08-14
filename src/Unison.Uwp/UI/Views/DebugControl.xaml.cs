using System;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class DebugControl : UserControl
    {
        public event EventHandler BackRequested;
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
                    _viewModel.BackRequested += (s, e) => BackRequested?.Invoke(this, e);
                    _viewModel.LogTextChanged += (s, e) =>
                    {
                        try
                        {
                            SessionLogScroller?.ChangeView(null, SessionLogScroller.ScrollableHeight, null, true);
                        }
                        catch
                        {
                        }
                    };
                }
                return _viewModel;
            }
        }

        public DebugControl()
        {
            this.InitializeComponent();
            StripToggleLabels(VerboseLoggingToggle);
            StripToggleLabels(SessionLoggingToggle);
            _logFlushTimer.Tick += (s, e) => ViewModel?.FlushPendingLogLines();
            _runtimeRefreshTimer.Tick += (s, e) => ViewModel?.RefreshRuntimeHealth();
            this.Loaded += DebugControl_Loaded;
            this.Unloaded += DebugControl_Unloaded;
        }

        private void DebugControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (Visibility == Visibility.Visible)
            {
                Activate();
            }
        }

        private void DebugControl_Unloaded(object sender, RoutedEventArgs e)
        {
            Deactivate();
        }

        public void Activate()
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

            ViewModel?.Activate(buildInfo);
            StripToggleLabels(VerboseLoggingToggle);
            StripToggleLabels(SessionLoggingToggle);
            _logFlushTimer.Start();
            _runtimeRefreshTimer.Start();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            ViewModel?.Deactivate();
            _logFlushTimer.Stop();
            _runtimeRefreshTimer.Stop();
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
