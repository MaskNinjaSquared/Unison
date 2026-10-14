using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using global::Unison.Core.Constants;
using global::Unison.Core.ViewModels;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Shell.Unison.Views
{
    /// <summary>
    /// Unison shell settings page (hero + sticky title). Opened via <c>INavigator.OpenSettings</c>.
    /// </summary>
    public sealed partial class SettingsView : Page
    {
        private const int ProfileLayoutMs = 320;
        private const int ProfileFadeMs = 180;

        private SettingsViewModel _viewModel;
        private bool _stickyVisible;
        private bool _syncingLanguageCombo;
        private bool _leaveHooked;
        private bool _profileExpanded;
        private bool _openProfileOnLoad;
        private bool _profileBusy;
        private bool _profileUsesExtended;
        private bool _heroRestSaved;
        private double _heroRestHeight;
        private double _settingsBodyRestHeight;
        private const double ExpandedPhotoMarginTop = 100;
        private const double ExpandedSectionsGap = 20;

        private Thickness _minimalRestMargin;
        private Thickness _extendedRestMargin;
        private HorizontalAlignment _extendedRestAlign;
        private VerticalAlignment _minimalRestVertical;
        private VerticalAlignment _extendedRestVertical;
        private Storyboard _stickyFadeStoryboard;
        private Storyboard _profileStoryboard;

        /// <summary>
        /// When true, the hero name, Your Profile, the settings back, and the Settings title hide,
        /// and the photo button on the current avatar shows.
        /// </summary>
        public bool ViewProfileState
        {
            get => (bool)GetValue(ViewProfileStateProperty);
            set => SetValue(ViewProfileStateProperty, value);
        }

        public static readonly DependencyProperty ViewProfileStateProperty =
            DependencyProperty.Register(
                nameof(ViewProfileState),
                typeof(bool),
                typeof(SettingsView),
                new PropertyMetadata(false, OnViewProfileStateChanged));

        private static void OnViewProfileStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            ((SettingsView)sender).ApplyProfileChrome((bool)args.NewValue);
        }

        public SettingsView()
        {
            InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;
            Loaded += SettingsView_Loaded;
            Unloaded += SettingsView_Unloaded;
        }

        private SettingsViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<SettingsViewModel>();
                    DataContext = _viewModel;
                }

                return _viewModel;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _openProfileOnLoad = e.Parameter is bool openProfile && openProfile;
            HookLeaveRequested();
            Activate();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            UnhookLeaveRequested();
        }

        private void HookLeaveRequested()
        {
            SettingsViewModel vm = ViewModel;
            if (vm == null || _leaveHooked)
            {
                return;
            }

            vm.LeaveRequested += ViewModel_LeaveRequested;
            _leaveHooked = true;
        }

        private void UnhookLeaveRequested()
        {
            if (!_leaveHooked || _viewModel == null)
            {
                return;
            }

            _viewModel.LeaveRequested -= ViewModel_LeaveRequested;
            _leaveHooked = false;
        }

        private void ViewModel_LeaveRequested(object sender, EventArgs e)
        {
            App.Services?.GetService<ShellViewModel>()?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
        }

        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            Activate();
            ViewModel?.Attach();
            SetStickyVisible(false, animate: false);
            if (_openProfileOnLoad)
            {
                _openProfileOnLoad = false;
                OpenProfile();
            }
        }

        /// <summary>Opens the expanded profile when settings is already on screen.</summary>
        public void OpenProfile()
        {
            if (_profileExpanded || _profileBusy)
            {
                return;
            }

            ExpandProfile();
        }

        private void SettingsView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_stickyFadeStoryboard != null)
            {
                _stickyFadeStoryboard.Stop();
                _stickyFadeStoryboard = null;
            }

            StopProfileStoryboard();
            _viewModel?.Detach();
        }

        private void Activate()
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

            Debug.WriteLine("[SettingsView] LanguageComboBox → index=" + index);
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

        private void YourProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_profileExpanded || _profileBusy)
            {
                return;
            }

            ExpandProfile();
        }

        private void ProfileSettings_BackRequested(object sender, RoutedEventArgs e)
        {
            if (!_profileExpanded || _profileBusy)
            {
                return;
            }

            CollapseProfile();
        }

        /// <summary>
        /// Hides the settings chrome and recenters the visible hero panel while
        /// <see cref="ViewProfileState"/> is on. Bindings hide the name, Your Profile,
        /// the settings back, and the Settings title.
        /// </summary>
        private void ApplyProfileChrome(bool viewing)
        {
            if (viewing)
            {
                if (!_heroRestSaved)
                {
                    _minimalRestMargin = HeroMinimalPanel.Margin;
                    _extendedRestMargin = HeroExtendedPanel.Margin;
                    _extendedRestAlign = HeroExtendedPanel.HorizontalAlignment;
                    _minimalRestVertical = HeroMinimalPanel.VerticalAlignment;
                    _extendedRestVertical = HeroExtendedPanel.VerticalAlignment;
                    _heroRestSaved = true;
                }

                // Photo sits at the top. Sections are placed separately, not as the next stack child.
                HeroMinimalPanel.VerticalAlignment = VerticalAlignment.Top;
                HeroExtendedPanel.VerticalAlignment = VerticalAlignment.Top;
                HeroMinimalPanel.Margin = new Thickness(0, ExpandedPhotoMarginTop, 0, 0);
                HeroExtendedPanel.Margin = new Thickness(0, ExpandedPhotoMarginTop, 0, 0);
                HeroExtendedPanel.HorizontalAlignment = HorizontalAlignment.Center;
                return;
            }

            if (!_heroRestSaved)
            {
                return;
            }

            HeroMinimalPanel.Margin = _minimalRestMargin;
            HeroExtendedPanel.Margin = _extendedRestMargin;
            HeroExtendedPanel.HorizontalAlignment = _extendedRestAlign;
            HeroMinimalPanel.VerticalAlignment = _minimalRestVertical;
            HeroExtendedPanel.VerticalAlignment = _extendedRestVertical;
        }

        /// <summary>
        /// Profile rows start 20px under the photo. They are not stacked after it.
        /// </summary>
        private void PlaceExpandedSections(FrameworkElement avatar)
        {
            double photoHeight = avatar.ActualHeight > 0 ? avatar.ActualHeight : 120;
            double top = ExpandedPhotoMarginTop + photoHeight + ExpandedSectionsGap;
            double viewport = RootGrid.ActualHeight - top;
            ProfileSettingsHost.VerticalAlignment = VerticalAlignment.Top;
            ProfileSettingsHost.Margin = new Thickness(0, top, 0, 0);
            ProfileSettingsHost.Height = viewport > 0 ? viewport : 0;
        }

        private void ResetExpandedSections()
        {
            ProfileSettingsHost.ClearValue(FrameworkElement.HeightProperty);
            ProfileSettingsHost.VerticalAlignment = VerticalAlignment.Bottom;
            ProfileSettingsHost.Margin = new Thickness(0);
        }

        /// <summary>
        /// Squashes the sections and moves the current avatar to the top of the hero.
        /// <see cref="ExpandedProfileState"/> is applied when that animation ends.
        /// </summary>
        private void ExpandProfile()
        {
            _profileBusy = true;
            _heroRestHeight = ProfileHero.ActualHeight;
            _settingsBodyRestHeight = SettingsBody.ActualHeight;
            _profileUsesExtended = HeroExtendedPanel.Visibility == Visibility.Visible;

            if (_heroRestHeight <= 0 || RootGrid.ActualHeight <= 0)
            {
                _profileExpanded = true;
                _profileBusy = false;
                ViewProfileState = true;
                PlaceExpandedSections(ActiveAvatarHost());
                ProfileSettingsHost.Visibility = Visibility.Visible;
                ProfileSettingsHost.Opacity = 1;
                VisualStateManager.GoToState(this, "ExpandedProfileState", false);
                return;
            }

            try { LeftContent.ChangeView(null, 0, null, true); } catch { }
            LeftContent.VerticalScrollMode = ScrollMode.Disabled;

            FrameworkElement avatar = ActiveAvatarHost();
            TranslateTransform translate = ActiveAvatarTranslate();
            Point start = CenterIn(avatar, ProfileHero);

            ViewProfileState = true;
            ProfileHero.UpdateLayout();

            Point laidOut = CenterIn(avatar, ProfileHero);
            translate.X = start.X - laidOut.X;
            translate.Y = start.Y - laidOut.Y;

            PlaceExpandedSections(avatar);
            ProfileSettingsHost.Visibility = Visibility.Visible;
            ProfileSettingsHost.Opacity = 0;
            ProfileSettingsHost.UpdateLayout();

            double heroTo = Math.Max(RootGrid.ActualHeight, _heroRestHeight);
            SettingsBody.Height = _settingsBodyRestHeight;
            ProfileHero.Height = _heroRestHeight;
            SettingsBodyScale.ScaleY = 1;
            SettingsBody.Opacity = 1;
            BeginLayoutStoryboard(expand: true, heroTo: heroTo);
        }

        private void CollapseProfile()
        {
            _profileBusy = true;
            if (_heroRestHeight <= 0)
            {
                _profileExpanded = false;
                _profileBusy = false;
                ViewProfileState = false;
                ResetExpandedSections();
                ProfileSettingsHost.Visibility = Visibility.Collapsed;
                ProfileSettingsHost.Opacity = 1;
                VisualStateManager.GoToState(this, "CollapsedProfile", false);
                return;
            }

            BeginHostFade(to: 0, onCompleted: ConcealHost_Completed);
        }

        private void BeginLayoutStoryboard(bool expand, double heroTo)
        {
            StopProfileStoryboard();

            TranslateTransform translate = ActiveAvatarTranslate();
            var storyboard = new Storyboard();
            double scaleTo = expand ? 0 : 1;
            double opacityTo = expand ? 0 : 1;
            double bodyTo = expand ? 0 : _settingsBodyRestHeight;
            storyboard.Children.Add(CreateAnimation(SettingsBodyScale, "ScaleY", scaleTo, ProfileLayoutMs, dependent: false));
            storyboard.Children.Add(CreateAnimation(SettingsBody, "Opacity", opacityTo, ProfileLayoutMs, dependent: false));
            storyboard.Children.Add(CreateAnimation(SettingsBody, "Height", bodyTo, ProfileLayoutMs, dependent: true));
            storyboard.Children.Add(CreateAnimation(ProfileHero, "Height", heroTo, ProfileLayoutMs, dependent: true));
            storyboard.Children.Add(CreateAnimation(translate, "X", 0, ProfileLayoutMs, dependent: false));
            storyboard.Children.Add(CreateAnimation(translate, "Y", 0, ProfileLayoutMs, dependent: false));
            if (expand)
            {
                storyboard.Children.Add(CreateAnimation(ProfileSettingsHost, "Opacity", 1, ProfileLayoutMs, dependent: false));
                storyboard.Completed += ExpandLayout_Completed;
            }
            else
            {
                storyboard.Completed += CollapseLayout_Completed;
            }

            _profileStoryboard = storyboard;
            storyboard.Begin();
        }

        private void ExpandLayout_Completed(object sender, object e)
        {
            TranslateTransform translate = ActiveAvatarTranslate();
            StopProfileStoryboard();
            SettingsBody.Height = 0;
            SettingsBody.Opacity = 0;
            SettingsBodyScale.ScaleY = 0;
            translate.X = 0;
            translate.Y = 0;
            ProfileSettingsHost.Opacity = 1;
            _profileExpanded = true;
            _profileBusy = false;
            SyncExpandedZone();
            VisualStateManager.GoToState(this, "ExpandedProfileState", false);
        }

        /// <summary>
        /// Expanded hero fills the window. A fixed height from open time would ignore later resizes.
        /// </summary>
        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_profileExpanded || _profileBusy)
            {
                return;
            }

            SyncExpandedZone();
        }

        private void SyncExpandedZone()
        {
            double height = RootGrid.ActualHeight;
            if (height <= 0)
            {
                return;
            }

            if (double.IsNaN(ProfileHero.Height) || Math.Abs(ProfileHero.Height - height) > 0.5)
            {
                ProfileHero.Height = height;
            }

            _profileUsesExtended = RootGrid.ActualWidth >= 700;
            PlaceExpandedSections(ActiveAvatarHost());
        }

        private void ConcealHost_Completed(object sender, object e)
        {
            StopProfileStoryboard();
            ProfileSettingsHost.Opacity = 0;

            FrameworkElement avatar = ActiveAvatarHost();
            TranslateTransform translate = ActiveAvatarTranslate();
            Point from = CenterIn(avatar, ProfileHero);
            translate.X = 0;
            translate.Y = 0;
            ViewProfileState = false;
            ProfileHero.UpdateLayout();

            Point resting = CenterIn(avatar, ProfileHero);
            translate.X = from.X - resting.X;
            translate.Y = from.Y - resting.Y;

            SettingsBody.Height = 0;
            SettingsBody.Opacity = 0;
            SettingsBodyScale.ScaleY = 0;
            VisualStateManager.GoToState(this, "CollapsedProfile", false);
            BeginLayoutStoryboard(expand: false, heroTo: _heroRestHeight);
        }

        private void CollapseLayout_Completed(object sender, object e)
        {
            TranslateTransform translate = ActiveAvatarTranslate();
            StopProfileStoryboard();
            ProfileHero.ClearValue(HeightProperty);
            SettingsBody.ClearValue(HeightProperty);
            SettingsBody.Opacity = 1;
            SettingsBodyScale.ScaleY = 1;
            translate.X = 0;
            translate.Y = 0;
            ResetExpandedSections();
            ProfileSettingsHost.Visibility = Visibility.Collapsed;
            ProfileSettingsHost.Opacity = 1;
            _profileExpanded = false;
            _profileBusy = false;
            LeftContent.VerticalScrollMode = ScrollMode.Enabled;
            SetStickyVisible(LeftContent.VerticalOffset >= 40, animate: false);
        }

        private FrameworkElement ActiveAvatarHost()
        {
            return _profileUsesExtended ? (FrameworkElement)ExtendedAvatarHost : MinimalAvatarHost;
        }

        private TranslateTransform ActiveAvatarTranslate()
        {
            return _profileUsesExtended ? ExtendedAvatarTranslate : MinimalAvatarTranslate;
        }

        private static Point CenterIn(FrameworkElement element, FrameworkElement relativeTo)
        {
            GeneralTransform transform = element.TransformToVisual(relativeTo);
            return transform.TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
        }

        private void BeginHostFade(double to, EventHandler<object> onCompleted)
        {
            StopProfileStoryboard();
            var storyboard = new Storyboard();
            storyboard.Children.Add(CreateAnimation(ProfileSettingsHost, "Opacity", to, ProfileFadeMs, dependent: false));
            storyboard.Completed += onCompleted;
            _profileStoryboard = storyboard;
            storyboard.Begin();
        }

        private void StopProfileStoryboard()
        {
            if (_profileStoryboard == null)
            {
                return;
            }

            _profileStoryboard.Completed -= ExpandLayout_Completed;
            _profileStoryboard.Completed -= CollapseLayout_Completed;
            _profileStoryboard.Completed -= ConcealHost_Completed;
            _profileStoryboard.Stop();
            _profileStoryboard = null;
        }

        private static DoubleAnimation CreateAnimation(
            DependencyObject target,
            string property,
            double to,
            int milliseconds,
            bool dependent)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(milliseconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = dependent
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }
    }
}
