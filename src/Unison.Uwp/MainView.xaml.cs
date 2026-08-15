using System;
using System.ComponentModel;
using System.Diagnostics;
using Windows.Graphics.Display;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.ViewModels;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;
using Unison.Uwp.UI.Views;

namespace Unison.Uwp
{
    /// <summary>
    /// AppShell: SplitView chrome + content Frame (Chats / Settings / Debug).
    /// Start / Login are root pages (see <see cref="INavigator"/>).
    /// </summary>
    public sealed partial class MainView : Page
    {
        /// <summary>Matches SettingsView ExtendedWide AdaptiveTrigger.</summary>
        private const double ExtendedWideMinWidth = 1000;

        public IWhatsAppService Service => App.GetWhatsAppService();

        private bool _uiEventsAttached;
        private bool _vmHooked;
        private bool _orientationHooked;
        private bool _syncingNavSelection;
        private bool _syncingPaneFromControl;
        private ShellViewModel _shellViewModel;
        private INavigator _navigator;

        private ShellViewModel ViewModel => _shellViewModel;

        public MainView()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Disabled;

            if (App.Services != null)
            {
                _shellViewModel = App.Services.GetRequiredService<ShellViewModel>();
                _navigator = App.Services.GetRequiredService<INavigator>();
                DataContext = _shellViewModel;
                HookViewModel();
            }

            this.Loaded += MainView_Loaded;
            this.Unloaded += MainView_Unloaded;
            this.SizeChanged += MainView_SizeChanged;
            // Do not subscribe PaneOpened/PaneClosed — TypedEventHandler<SplitView>
            // casts EETypeRva on W10M Creators (.NET Native) during Navigate.
        }

        /// <summary>
        /// Keep VM IsPaneOpen aligned when we read the control (e.g. hamburger toggle).
        /// Avoid PaneOpened/PaneClosed on Mobile — those event subscriptions crash.
        /// </summary>
        private void SyncViewModelPaneOpenFromControl(bool open)
        {
            if (ViewModel == null || _syncingPaneFromControl)
            {
                return;
            }

            if (ViewModel.IsPaneOpen == open)
            {
                return;
            }

            _syncingPaneFromControl = true;
            try
            {
                ViewModel.IsPaneOpen = open;
            }
            finally
            {
                _syncingPaneFromControl = false;
            }
        }

        private void HookViewModel()
        {
            if (_vmHooked || _shellViewModel == null)
            {
                return;
            }

            _shellViewModel.PropertyChanged += ShellViewModel_PropertyChanged;
            _shellViewModel.SessionUiResetRequested -= ShellViewModel_SessionUiResetRequested;
            _shellViewModel.SessionUiResetRequested += ShellViewModel_SessionUiResetRequested;
            if (_navigator != null)
            {
                _navigator.ShellNavigated -= Navigator_ShellNavigated;
                _navigator.ShellNavigated += Navigator_ShellNavigated;
            }
            _vmHooked = true;
            ApplySystemBackButton();
        }

        private void UnhookViewModel()
        {
            if (!_vmHooked || _shellViewModel == null)
            {
                return;
            }

            _shellViewModel.PropertyChanged -= ShellViewModel_PropertyChanged;
            _shellViewModel.SessionUiResetRequested -= ShellViewModel_SessionUiResetRequested;
            if (_navigator != null)
            {
                _navigator.ShellNavigated -= Navigator_ShellNavigated;
            }
            _vmHooked = false;
        }

        /// <summary>
        /// Frame.Content is authoritative after Navigated — always re-wire Chats menu here.
        /// </summary>
        private void Navigator_ShellNavigated(object sender, string route)
        {
            var chats = ShellContentFrame?.Content as ChatsView;
            Debug.WriteLine(
                "[MainView] ShellNavigated route=" + (route ?? "?") +
                " content=" + (ShellContentFrame?.Content?.GetType().Name ?? "null"));
            WireChatsViewMenu(chats);
        }

        private void ShellViewModel_SessionUiResetRequested(object sender, EventArgs e)
        {
            try
            {
                NavigationCacheMode = NavigationCacheMode.Disabled;
            }
            catch
            {
            }

            var chats = ShellContentFrame?.Content as ChatsView;
            if (chats != null)
            {
                _ = chats.ResetForLoggedOutAsync();
            }

            try
            {
                _navigator?.PurgeShellNavigation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainView] PurgeShellNavigation: " + ex.Message);
            }
        }

        private void AttachUiEvents()
        {
            if (_uiEventsAttached)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += MainView_BackRequested;
            _uiEventsAttached = true;
        }

        private void MainView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_uiEventsAttached)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= MainView_BackRequested;
                _uiEventsAttached = false;
            }

            UnhookViewModel();
        }

        private void MainView_Loaded(object sender, RoutedEventArgs e)
        {
            (Service as WhatsAppService)?.AttachUiDispatcher(Dispatcher);
            AttachUiEvents();

            if (_shellViewModel == null && App.Services != null)
            {
                _shellViewModel = App.Services.GetRequiredService<ShellViewModel>();
                _navigator = App.Services.GetRequiredService<INavigator>();
                DataContext = _shellViewModel;
                HookViewModel();
            }

            if (_shellViewModel == null)
            {
                return;
            }

            _navigator?.AttachShellFrame(ShellContentFrame);
            EnsureShellContent();

            _shellViewModel.ReportWindowNarrow(IsNarrowWindow());
            EnsureOrientationHook();
            ApplySystemBackButton();
            ViewModel.RefreshUserInfo();
            EnsureOpenPendingChatFromActivation();
        }

        private void EnsureShellContent()
        {
            if (ShellContentFrame.Content != null)
            {
                WireChatsViewMenu(ShellContentFrame.Content as ChatsView);
                return;
            }

            string section = ViewModel?.ActiveSection ?? NavigationRoutes.Chats;
            ViewModel?.NavigateToSectionCommand.Execute(section);
            WireChatsViewMenu(ShellContentFrame.Content as ChatsView);
        }

        private ChatsView _wiredChatsMenu;

        private void WireChatsViewMenu(ChatsView chats)
        {
            if (_wiredChatsMenu != null)
            {
                _wiredChatsMenu.MenuClicked -= ChatsView_MenuClicked;
                _wiredChatsMenu = null;
            }

            if (chats == null)
            {
                return;
            }

            chats.MenuClicked += ChatsView_MenuClicked;
            _wiredChatsMenu = chats;
            Debug.WriteLine("[MainView] WireChatsViewMenu attached");
        }

        private void ShellViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ShowSystemBackButton))
            {
                ApplySystemBackButton();
            }
            else if (e.PropertyName == nameof(ShellViewModel.IsPaneOpen))
            {
                if (_syncingPaneFromControl)
                {
                    return;
                }

                EnsurePhoneHandsetOverlayMode();
                if (RootSplitView != null && RootSplitView.IsPaneOpen != ViewModel.IsPaneOpen)
                {
                    RootSplitView.IsPaneOpen = ViewModel.IsPaneOpen;
                }
            }
            else if (e.PropertyName == nameof(ShellViewModel.ActiveSection))
            {
                SyncNavSelection();
                ApplySystemBackButton();
                // Handset must stay Overlay — VSM Extended may flip DisplayMode to Inline
                // after Settings, and SyncInlineSidebar would immediately close an open pane.
                EnsurePhoneHandsetOverlayMode();
                WireChatsViewMenu(ShellContentFrame.Content as ChatsView);
                if (!IsPhoneHandset())
                {
                    SyncInlineSidebarForWindowWidth();
                }

                // Overlay hamburger: close after any section navigation (menu click / back).
                if (IsNarrowWindow() || IsPhoneHandset())
                {
                    SetPaneOpen(false);
                }
            }
            else if (e.PropertyName == nameof(ShellViewModel.PendingOpenChatJid))
            {
                // Toast / tile while already in AppShell — ensure Chats is shown and open.
                EnsureOpenPendingChatFromActivation();
            }
        }

        /// <summary>Secondary tile / toast deep-link while AppShell is already alive.</summary>
        private void EnsureOpenPendingChatFromActivation()
        {
            if (ViewModel == null || string.IsNullOrWhiteSpace(ViewModel.PendingOpenChatJid))
            {
                return;
            }

            try
            {
                ViewModel.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
                EnsureShellContent();
                var chats = ShellContentFrame?.Content as ChatsView;
                WireChatsViewMenu(chats);
                chats?.RequestOpenPendingDeepLink();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainView] EnsureOpenPendingChatFromActivation: " + ex.Message);
            }
        }

        private void ApplySystemBackButton()
        {
            bool show = ViewModel?.ShowSystemBackButton == true;
            var chats = ShellContentFrame.Content as ChatsView;
            if (!show && chats != null && ViewModel != null &&
                ((ViewModel.IsNarrowWindow && ViewModel.ChatPane == ShellViewModel.PaneNarrowDetail) ||
                 (!ViewModel.IsNarrowWindow && ViewModel.HasActiveChat)))
            {
                show = true;
            }

            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                show ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        private void SyncNavSelection()
        {
            string section = ViewModel?.ActiveSection ?? NavigationRoutes.Chats;
            bool bottomSection =
                string.Equals(section, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(section, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase);

            _syncingNavSelection = true;
            try
            {
                if (bottomSection)
                {
                    if (NavListView != null)
                    {
                        NavListView.SelectedItem = null;
                    }

                    SelectNavTag(BottomNavListView, section);
                }
                else
                {
                    if (BottomNavListView != null)
                    {
                        BottomNavListView.SelectedItem = null;
                    }

                    SelectNavTag(NavListView, section);
                }
            }
            finally
            {
                _syncingNavSelection = false;
            }
        }

        private static void SelectNavTag(ListView list, string tag)
        {
            if (list == null)
            {
                return;
            }

            foreach (var item in list.Items)
            {
                if (item is ListViewItem lvi &&
                    string.Equals(lvi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                {
                    list.SelectedItem = lvi;
                    return;
                }
            }
        }

        private void MainView_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            // Chat detail / list chrome first.
            var chats = ShellContentFrame.Content as ChatsView;
            if (chats != null && chats.TryHandleBack())
            {
                e.Handled = true;
                return;
            }

            // Settings / Debug (and shell back stack) via NavigationService.
            if (ViewModel != null && ViewModel.TryHandleShellBack())
            {
                e.Handled = true;
            }
        }

        private void ChatsView_MenuClicked(object sender, EventArgs e)
        {
            if (ViewModel == null)
            {
                RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
                Debug.WriteLine("[MainView] MenuClicked (no VM) → pane=" + RootSplitView.IsPaneOpen);
                return;
            }

            EnsurePhoneHandsetOverlayMode();

            bool visualBefore = RootSplitView != null && RootSplitView.IsPaneOpen;
            bool vmBefore = ViewModel.IsPaneOpen;

            // If light-dismiss closed the pane while VM still thinks it is open,
            // adopt the visual state first so one tap opens instead of "closing" nothing.
            if (RootSplitView != null && ViewModel.IsPaneOpen != RootSplitView.IsPaneOpen)
            {
                SyncViewModelPaneOpenFromControl(RootSplitView.IsPaneOpen);
            }

            bool next = !ViewModel.IsPaneOpen;
            ViewModel.IsPaneOpen = next;
            if (RootSplitView != null)
            {
                RootSplitView.IsPaneOpen = next;
            }

            Debug.WriteLine(
                "[MainView] MenuClicked toggle pane: vm " + vmBefore + "→" + ViewModel.IsPaneOpen +
                " visual " + visualBefore + "→" + (RootSplitView != null && RootSplitView.IsPaneOpen) +
                " mode=" + (RootSplitView != null ? RootSplitView.DisplayMode.ToString() : "?") +
                " section=" + (ViewModel.ActiveSection ?? "?"));

            // One-frame reassert: DisplayMode switches / layout can clear IsPaneOpen.
            if (next)
            {
                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (ViewModel == null || RootSplitView == null || !ViewModel.IsPaneOpen)
                    {
                        return;
                    }

                    EnsurePhoneHandsetOverlayMode();
                    if (!RootSplitView.IsPaneOpen)
                    {
                        Debug.WriteLine("[MainView] Reassert IsPaneOpen=true after layout");
                        RootSplitView.IsPaneOpen = true;
                    }
                });
            }
        }

        private void NavListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingNavSelection)
            {
                return;
            }

            var sourceList = sender as ListView;
            var targetList = sourceList == NavListView ? BottomNavListView : NavListView;

            if (sourceList?.SelectedItem == null)
            {
                return;
            }

            if (targetList != null)
            {
                _syncingNavSelection = true;
                try
                {
                    targetList.SelectedItem = null;
                }
                finally
                {
                    _syncingNavSelection = false;
                }
            }

            if (sourceList.SelectedItem is ListViewItem item)
            {
                if (!item.IsEnabled)
                {
                    return;
                }

                string tag = item.Tag?.ToString();
                if (string.IsNullOrEmpty(tag))
                {
                    return;
                }

                ViewModel?.NavigateToSectionCommand.Execute(tag);
            }
        }

        private void HamburgerButton_Click(object sender, RoutedEventArgs e)
        {
            ChatsView_MenuClicked(sender, EventArgs.Empty);
        }

        /// <summary>Kept for callers that still expect the old API.</summary>
        public void ShowConnectedPanel()
        {
            ViewModel?.EnterConnectedSurface();
        }

        /// <summary>Kept for callers that still expect the old API.</summary>
        public void ShowLoginPanel()
        {
            ViewModel?.EnterLoginSurface();
        }

        private void LayoutStates_CurrentStateChanged(object sender, VisualStateChangedEventArgs e)
        {
            bool enteredMinimal =
                e?.NewState != null &&
                string.Equals(e.NewState.Name, "Minimal", StringComparison.Ordinal) &&
                (e.OldState == null || !string.Equals(e.OldState.Name, "Minimal", StringComparison.Ordinal));

            ViewModel?.ReportWindowNarrow(IsNarrowWindow());

            // Close overlay pane once when entering Minimal (not on every AdaptiveTrigger re-eval).
            if (enteredMinimal && ViewModel != null && ViewModel.IsPaneOpen)
            {
                ViewModel.IsPaneOpen = false;
                RootSplitView.IsPaneOpen = false;
            }

            QueuePhoneHandsetShellChrome();
            SyncInlineSidebarForWindowWidth();
        }

        private void MainView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Skip first layout; only react to real width changes.
            if (e.PreviousSize.Width <= 0 ||
                Math.Abs(e.PreviousSize.Width - e.NewSize.Width) < 0.5)
            {
                return;
            }

            SyncInlineSidebarForWindowWidth(e.NewSize.Width);
        }

        /// <summary>
        /// Inline pane pushes content. Auto-close only in Extended (720-999).
        /// ExtendedWide (&gt;=1000): never auto-close; keep/open when Settings is active.
        /// Overlay never pushes — leave alone.
        /// </summary>
        private void SyncInlineSidebarForWindowWidth(double? widthHint = null)
        {
            if (RootSplitView == null)
            {
                return;
            }

            // Phone handset chrome is Overlay-only; auto-closing Inline panes must not apply.
            if (IsPhoneHandset())
            {
                return;
            }

            if (RootSplitView.DisplayMode != SplitViewDisplayMode.Inline)
            {
                return;
            }

            double width = widthHint ?? GetLayoutWindowWidth();
            bool extendedWide = IsExtendedWideLayout(width);

            if (extendedWide && IsSettingsSection())
            {
                SetPaneOpen(true);
                return;
            }

            // At ExtendedWide do not yank the pane on resize.
            if (extendedWide)
            {
                return;
            }

            if (RootSplitView.IsPaneOpen)
            {
                SetPaneOpen(false);
            }
        }

        private bool IsExtendedWideLayout(double width)
        {
            if (LayoutStates?.CurrentState != null &&
                string.Equals(LayoutStates.CurrentState.Name, "ExtendedWide", StringComparison.Ordinal))
            {
                return true;
            }

            return width >= ExtendedWideMinWidth;
        }

        /// <summary>Same metric AdaptiveTrigger MinWindowWidth uses.</summary>
        private double GetLayoutWindowWidth()
        {
            try
            {
                double bounds = Window.Current?.Bounds.Width ?? 0;
                if (bounds > 0)
                {
                    return bounds;
                }
            }
            catch
            {
                // Fall through to ActualWidth.
            }

            return ActualWidth;
        }

        private bool IsSettingsSection()
        {
            if (ViewModel != null &&
                string.Equals(ViewModel.ActiveSection, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return ShellContentFrame?.Content is SettingsView;
        }

        private void SetPaneOpen(bool open)
        {
            if (RootSplitView != null)
            {
                RootSplitView.IsPaneOpen = open;
            }

            if (ViewModel != null && ViewModel.IsPaneOpen != open)
            {
                ViewModel.IsPaneOpen = open;
            }
        }

        private void EnsureOrientationHook()
        {
            if (_orientationHooked)
            {
                return;
            }

            try
            {
                DisplayInformation.GetForCurrentView().OrientationChanged -= MainView_OrientationChanged;
                DisplayInformation.GetForCurrentView().OrientationChanged += MainView_OrientationChanged;
                _orientationHooked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainView] Orientation hook failed: " + ex.Message);
            }
        }

        private void MainView_OrientationChanged(DisplayInformation sender, object args)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ViewModel?.OnDisplayOrientationChanged();
                ViewModel?.ReportWindowNarrow(IsNarrowWindow());
                QueuePhoneHandsetShellChrome();
            });
        }

        private void QueuePhoneHandsetShellChrome()
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                EnsurePhoneHandsetOverlayMode();
                // Do not call OnDisplayOrientationChanged here — that closes the hamburger
                // after the user opened it (menu button / TogglePane race).
                if (ViewModel != null)
                {
                    RootSplitView.IsPaneOpen = ViewModel.IsPaneOpen;
                }
                else if (IsPhoneHandset())
                {
                    RootSplitView.IsPaneOpen = false;
                }

                // Re-apply after chrome sync so ExtendedWide Settings is not left closed.
                SyncInlineSidebarForWindowWidth();
            });
        }

        private bool IsPhoneHandset()
        {
            try
            {
                var info = App.Services?.GetService<ISystemInfoProvider>();
                return info != null && info.IsMobile() && !info.IsContinuum();
            }
            catch
            {
                return false;
            }
        }

        private void EnsurePhoneHandsetOverlayMode()
        {
            try
            {
                if (!IsPhoneHandset())
                {
                    return;
                }

                if (RootSplitView.DisplayMode != SplitViewDisplayMode.Overlay)
                {
                    Debug.WriteLine(
                        "[MainView] Force Overlay (was " + RootSplitView.DisplayMode + ")");
                    RootSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainView] EnsurePhoneHandsetOverlayMode: " + ex.Message);
            }
        }

        private bool IsNarrowWindow()
        {
            if (LayoutStates?.CurrentState != null)
            {
                return LayoutStates.CurrentState.Name == "Minimal";
            }

            return ActualWidth > 0 && ActualWidth < 720;
        }
    }
}
