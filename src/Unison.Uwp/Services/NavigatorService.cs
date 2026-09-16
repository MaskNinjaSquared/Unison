using System;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.Uwp.Services.Themes;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Services
{
    public class NavigatorService : INavigator
    {
        private readonly Frame _rootFrame;
        private readonly IShellNavigationStrategy _shellNav;
        private Frame _shellFrame;

        public NavigatorService(Frame frame, IShellNavigationStrategy shellNav)
        {
            _rootFrame = frame ?? throw new ArgumentNullException(nameof(frame));
            _shellNav = shellNav ?? throw new ArgumentNullException(nameof(shellNav));
        }

        public bool CanGoBack => _rootFrame?.CanGoBack ?? false;

        public bool CanGoBackInShell => _shellFrame?.CanGoBack ?? false;

        public string CurrentShellRoute => ResolveShellRoute(_shellFrame?.Content);

        public event EventHandler<string> ShellNavigated;

        public void AttachShellFrame(object frame)
        {
            if (_shellFrame != null)
            {
                _shellFrame.Navigated -= ShellFrame_Navigated;
            }

            _shellFrame = frame as Frame;

            if (_shellFrame != null)
            {
                _shellFrame.Navigated += ShellFrame_Navigated;
                RaiseShellNavigated();
            }
        }

        public void Navigate(NavigationDestination destination, object parameter = null)
        {
            NavigateCore(_rootFrame, _shellNav.ResolveRootPage(destination), parameter, clearStack: false);
        }

        public void NavigateAndClear(NavigationDestination destination, object parameter = null)
        {
            NavigateCore(_rootFrame, _shellNav.ResolveRootPage(destination), parameter, clearStack: true);
        }

        public void GoBack()
        {
            if (_rootFrame?.CanGoBack == true)
            {
                _rootFrame.GoBack();
            }
        }

        public void ClearBackStack()
        {
            ClearFrameBackStack(_rootFrame);
        }

        public void NavigateInShell(NavigationDestination destination, object parameter = null)
        {
            if (destination == NavigationDestination.Settings)
            {
                OpenSettings();
                return;
            }

            NavigateCore(_shellFrame, _shellNav.ResolveShellPage(destination), parameter, clearStack: false);
        }

        public void NavigateInShellAndClear(NavigationDestination destination, object parameter = null)
        {
            if (destination == NavigationDestination.Settings)
            {
                OpenSettings();
                return;
            }

            NavigateCore(_shellFrame, _shellNav.ResolveShellPage(destination), parameter, clearStack: true);
        }

        public void OpenSettings()
        {
            if (_shellFrame == null)
            {
                throw new InvalidOperationException("Shell frame is not ready for OpenSettings.");
            }

            _shellNav.OpenSettings(_shellFrame);
            RaiseShellNavigated();
        }

        public void GoBackInShell()
        {
            if (_shellFrame?.CanGoBack == true)
            {
                _shellFrame.GoBack();
            }
        }

        public void PurgeShellNavigation()
        {
            if (_shellFrame == null)
            {
                return;
            }

            try
            {
                if (_shellFrame.Content is Page shellPage)
                {
                    shellPage.NavigationCacheMode = NavigationCacheMode.Disabled;
                }
            }
            catch
            {
            }

            ClearFrameBackStack(_shellFrame);
        }

        private void ShellFrame_Navigated(object sender, NavigationEventArgs e)
        {
            RaiseShellNavigated();
        }

        private void RaiseShellNavigated()
        {
            ShellNavigated?.Invoke(this, CurrentShellRoute);
        }

        private string ResolveShellRoute(object content)
        {
            if (_shellNav.TryResolveShellDestination(content, out NavigationDestination destination))
            {
                return NavigationRoutes.ToRouteKey(destination);
            }

            return null;
        }

        private static void NavigateCore(
            Frame frame,
            Type pageType,
            object parameter,
            bool clearStack)
        {
            if (frame == null)
            {
                throw new InvalidOperationException("Navigation frame is not ready.");
            }

            if (pageType == null)
            {
                throw new ArgumentNullException(nameof(pageType));
            }

            // Same page already showing — still remount when clearing so logout → login → shell
            // never reuses a NavigationCacheMode.Required instance with stale ViewModels.
            if (frame.Content != null && frame.Content.GetType() == pageType)
            {
                if (clearStack)
                {
                    try
                    {
                        if (frame.Content is Page current)
                        {
                            current.NavigationCacheMode = NavigationCacheMode.Disabled;
                        }
                    }
                    catch
                    {
                    }

                    ClearFrameBackStack(frame);
                    frame.Navigate(pageType, parameter);
                    ClearFrameBackStack(frame);
                }

                return;
            }

            try
            {
                if (clearStack && frame.Content is Page leaving)
                {
                    leaving.NavigationCacheMode = NavigationCacheMode.Disabled;
                }
            }
            catch
            {
            }

            frame.Navigate(pageType, parameter);
            if (clearStack)
            {
                ClearFrameBackStack(frame);
            }
        }

        private static void ClearFrameBackStack(Frame frame)
        {
            if (frame == null)
            {
                return;
            }

            try
            {
                frame.BackStack.Clear();
                frame.ForwardStack.Clear();
            }
            catch
            {
                // Older builds: ignore if stacks are locked mid-navigation.
            }
        }
    }
}
