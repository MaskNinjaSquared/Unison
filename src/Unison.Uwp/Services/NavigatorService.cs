using System;
using System.Collections.Generic;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unison.Uwp.Services
{
    public class NavigatorService : INavigator
    {
        private readonly Frame _rootFrame;
        private Frame _shellFrame;

        private static readonly Dictionary<string, Type> RootRoutes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { NavigationRoutes.Boot, typeof(UI.Views.BootView) },
                { NavigationRoutes.Start, typeof(UI.Views.StartView) },
                { NavigationRoutes.Login, typeof(UI.Views.LoginView) },
                { NavigationRoutes.AppShell, typeof(MainView) },
                { NavigationRoutes.Main, typeof(MainView) },
            };

        private static readonly Dictionary<string, Type> ShellRoutes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { NavigationRoutes.Chats, typeof(UI.Views.ChatsView) },
                { NavigationRoutes.Settings, typeof(UI.Views.SettingsView) },
                { NavigationRoutes.Debug, typeof(UI.Views.DebugView) },
            };

        public NavigatorService(Frame frame)
        {
            _rootFrame = frame ?? throw new ArgumentNullException(nameof(frame));
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

        public void Navigate(string destination, object parameter = null)
        {
            NavigateCore(_rootFrame, RootRoutes, destination, parameter, clearStack: false);
        }

        public void NavigateAndClear(string destination, object parameter = null)
        {
            NavigateCore(_rootFrame, RootRoutes, destination, parameter, clearStack: true);
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

        public void NavigateInShell(string destination, object parameter = null)
        {
            NavigateCore(_shellFrame, ShellRoutes, destination, parameter, clearStack: false);
        }

        public void NavigateInShellAndClear(string destination, object parameter = null)
        {
            NavigateCore(_shellFrame, ShellRoutes, destination, parameter, clearStack: true);
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

        private static string ResolveShellRoute(object content)
        {
            if (content == null)
            {
                return null;
            }

            if (content is UI.Views.SettingsView)
            {
                return NavigationRoutes.Settings;
            }

            if (content is UI.Views.DebugView)
            {
                return NavigationRoutes.Debug;
            }

            if (content is UI.Views.ChatsView)
            {
                return NavigationRoutes.Chats;
            }

            return null;
        }

        private static void NavigateCore(
            Frame frame,
            Dictionary<string, Type> routes,
            string destination,
            object parameter,
            bool clearStack)
        {
            if (frame == null)
            {
                throw new InvalidOperationException("Navigation frame is not ready for '" + destination + "'.");
            }

            if (!routes.TryGetValue(destination, out var pageType))
            {
                throw new ArgumentException("Unknown route: '" + destination + "'");
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
