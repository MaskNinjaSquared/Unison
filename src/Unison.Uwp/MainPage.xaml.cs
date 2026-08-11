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
    public sealed partial class MainPage : Page
    {
        public IWhatsAppService Service => App.GetWhatsAppService();

        private bool _uiEventsAttached;
        private bool _vmHooked;
        private bool _orientationHooked;
        private ShellViewModel _shellViewModel;
        private INavigator _navigator;

        private ShellViewModel ViewModel => _shellViewModel;

        public MainPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Required;

            if (App.Services != null)
            {
                _shellViewModel = App.Services.GetRequiredService<ShellViewModel>();
                _navigator = App.Services.GetRequiredService<INavigator>();
                DataContext = _shellViewModel;
                HookViewModel();
            }

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;
        }

        private void HookViewModel()
        {
            if (_vmHooked || _shellViewModel == null)
            {
                return;
            }

            _shellViewModel.PropertyChanged += ShellViewModel_PropertyChanged;
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
            _vmHooked = false;
        }

        private void AttachUiEvents()
        {
            if (_uiEventsAttached)
            {
                return;
            }

            SystemNavigationManager.GetForCurrentView().BackRequested += MainPage_BackRequested;
            _uiEventsAttached = true;
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_uiEventsAttached)
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= MainPage_BackRequested;
                _uiEventsAttached = false;
            }

            UnhookViewModel();
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
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
        }

        private void EnsureShellContent()
        {
            if (ShellContentFrame.Content != null)
            {
                WireChatsPageMenu(ShellContentFrame.Content as ChatsPage);
                return;
            }

            string section = ViewModel?.ActiveSection ?? NavigationRoutes.Chats;
            ViewModel?.NavigateToSectionCommand.Execute(section);
            WireChatsPageMenu(ShellContentFrame.Content as ChatsPage);
        }

        private void WireChatsPageMenu(ChatsPage chats)
        {
            if (chats == null)
            {
                return;
            }

            chats.MenuClicked -= ChatsPage_MenuClicked;
            chats.MenuClicked += ChatsPage_MenuClicked;
        }

        private void ShellViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ShellViewModel.ShowSystemBackButton))
            {
                ApplySystemBackButton();
            }
            else if (e.PropertyName == nameof(ShellViewModel.IsPaneOpen))
            {
                EnsurePhoneHandsetOverlayMode();
                RootSplitView.IsPaneOpen = ViewModel.IsPaneOpen;
            }
            else if (e.PropertyName == nameof(ShellViewModel.ActiveSection))
            {
                SyncNavSelection();
                ApplySystemBackButton();
                WireChatsPageMenu(ShellContentFrame.Content as ChatsPage);
            }
        }

        private void ApplySystemBackButton()
        {
            bool show = ViewModel?.ShowSystemBackButton == true;
            var chats = ShellContentFrame.Content as ChatsPage;
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
            SelectNavTag(NavListView, section);
            SelectNavTag(BottomNavListView, section);
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

            if (string.Equals(tag, NavigationRoutes.Chats, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tag, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase))
            {
                // Other list may own the selection.
            }
        }

        private void MainPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
            {
                return;
            }

            string section = ViewModel?.ActiveSection;
            if (string.Equals(section, NavigationRoutes.Debug, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(section, NavigationRoutes.Settings, StringComparison.OrdinalIgnoreCase))
            {
                ViewModel?.NavigateToSectionCommand.Execute(NavigationRoutes.Chats);
                NavListView.SelectedIndex = 0;
                e.Handled = true;
                return;
            }

            var chats = ShellContentFrame.Content as ChatsPage;
            if (chats != null && chats.TryHandleBack())
            {
                e.Handled = true;
            }
        }

        private void ChatsPage_MenuClicked(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.TogglePaneCommand.Execute(null);
                EnsurePhoneHandsetOverlayMode();
                RootSplitView.IsPaneOpen = ViewModel.IsPaneOpen;
            }
            else
            {
                RootSplitView.IsPaneOpen = !RootSplitView.IsPaneOpen;
            }
        }

        private void NavListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sourceList = sender as ListView;
            var targetList = sourceList == NavListView ? BottomNavListView : NavListView;

            if (sourceList?.SelectedItem == null)
            {
                return;
            }

            if (targetList != null)
            {
                targetList.SelectedItem = null;
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
            ChatsPage_MenuClicked(sender, EventArgs.Empty);
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
            ViewModel?.ReportWindowNarrow(IsNarrowWindow());
            QueuePhoneHandsetShellChrome();
        }

        private void EnsureOrientationHook()
        {
            if (_orientationHooked)
            {
                return;
            }

            try
            {
                DisplayInformation.GetForCurrentView().OrientationChanged -= MainPage_OrientationChanged;
                DisplayInformation.GetForCurrentView().OrientationChanged += MainPage_OrientationChanged;
                _orientationHooked = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainPage] Orientation hook failed: " + ex.Message);
            }
        }

        private void MainPage_OrientationChanged(DisplayInformation sender, object args)
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
                ViewModel?.OnDisplayOrientationChanged();
                if (ViewModel != null)
                {
                    RootSplitView.IsPaneOpen = ViewModel.IsPaneOpen;
                }
                else if (IsPhoneHandset())
                {
                    RootSplitView.IsPaneOpen = false;
                }
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
                    RootSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MainPage] EnsurePhoneHandsetOverlayMode: " + ex.Message);
            }
        }

        private bool IsNarrowWindow()
        {
            if (LayoutStates?.CurrentState != null)
            {
                return LayoutStates.CurrentState.Name == "NarrowState";
            }

            return ActualWidth > 0 && ActualWidth < 720;
        }
    }
}
