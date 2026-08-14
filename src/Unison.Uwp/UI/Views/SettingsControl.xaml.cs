using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Settings surface: profile hero in scroll + sticky compact title bar (Imgur AccountView-style).
    /// </summary>
    public sealed partial class SettingsControl : UserControl
    {
        public event EventHandler LeaveRequested;

        private SettingsViewModel _viewModel;
        private bool _stickyVisible;
        private bool _syncingLanguageCombo;
        private Storyboard _stickyFadeStoryboard;

        public SettingsControl()
        {
            this.InitializeComponent();
            this.Loaded += SettingsControl_Loaded;
            this.Unloaded += SettingsControl_Unloaded;
        }

        private SettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
                    DataContext = _viewModel;
                    _viewModel.LeaveRequested += (s, e) => LeaveRequested?.Invoke(this, e);
                }
                return _viewModel;
            }
        }

        private void SettingsControl_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            SetStickyVisible(false, animate: false);
        }

        private void SettingsControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_stickyFadeStoryboard != null)
            {
                _stickyFadeStoryboard.Stop();
                _stickyFadeStoryboard = null;
            }
        }

        public void Activate()
        {
            string version = "?";
            try
            {
                var v = Package.Current.Id.Version;
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
            }

            // Initialize / DataContext / ItemsSource rebinds fire SelectionChanged —
            // suppress until the persisted index is restored.
            _syncingLanguageCombo = true;
            try
            {
                ViewModel?.Initialize(version);
                SyncLanguageComboSelection();
            }
            finally
            {
                _syncingLanguageCombo = false;
            }

            SetStickyVisible(false, animate: false);
            try { LeftContent.ChangeView(null, 0, null, true); } catch { }
        }

        /// <summary>
        /// UWP ComboBox often clears selection when ItemsSource binds after SelectedIndex/Item.
        /// Force the persisted language after Initialize refreshes bindings.
        /// </summary>
        private void SyncLanguageComboSelection()
        {
            if (ViewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = ViewModel.SelectedLanguageIndex;
            if (index < 0 || index >= LanguageComboBox.Items.Count)
            {
                return;
            }

            bool wasSyncing = _syncingLanguageCombo;
            _syncingLanguageCombo = true;
            try
            {
                LanguageComboBox.SelectedIndex = index;
            }
            finally
            {
                _syncingLanguageCombo = wasSyncing;
            }
        }

        /// <summary>
        /// Use live SelectedIndex — InvokeCommandAction CommandParameter often ships a stale index.
        /// </summary>
        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingLanguageCombo || ViewModel == null || LanguageComboBox == null)
            {
                return;
            }

            int index = LanguageComboBox.SelectedIndex;
            if (index < 0)
            {
                return;
            }

            // Rebind noise often re-selects the already-saved language — never restart for that.
            if (index == ViewModel.SelectedLanguageIndex)
            {
                return;
            }

            Debug.WriteLine("[SettingsControl] LanguageComboBox → index=" + index);
            if (ViewModel.ChangeLanguageCommand != null &&
                ViewModel.ChangeLanguageCommand.CanExecute(index))
            {
                ViewModel.ChangeLanguageCommand.Execute(index);
            }
        }

        private void ProfileHero_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Clip tiled wallpaper to hero bounds (Canvas tiles must not paint into gray settings).
            if (ProfileHero.ActualWidth <= 0 || ProfileHero.ActualHeight <= 0)
            {
                return;
            }

            ProfileHero.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, ProfileHero.ActualWidth, ProfileHero.ActualHeight)
            };
        }

        private void LeftContent_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Show sticky bar once the in-hero back/title row (~40px) has scrolled out of reach.
            const double threshold = 40;
            bool show = LeftContent.VerticalOffset >= threshold;
            if (show == _stickyVisible)
            {
                return;
            }

            SetStickyVisible(show, animate: true);
        }

        private void SetStickyVisible(bool visible, bool animate)
        {
            _stickyVisible = visible;
            StickyHeaderBar.IsHitTestVisible = visible;

            double target = visible ? 1.0 : 0.0;
            if (!animate)
            {
                if (_stickyFadeStoryboard != null)
                {
                    _stickyFadeStoryboard.Stop();
                    _stickyFadeStoryboard = null;
                }

                StickyHeaderBar.Opacity = target;
                return;
            }

            if (_stickyFadeStoryboard != null)
            {
                _stickyFadeStoryboard.Stop();
            }

            var animation = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, StickyHeaderBar);
            Storyboard.SetTargetProperty(animation, "Opacity");

            _stickyFadeStoryboard = new Storyboard();
            _stickyFadeStoryboard.Children.Add(animation);
            _stickyFadeStoryboard.Begin();
        }
    }
}
