using System;
using System.Collections.Generic;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services
{
    public class NavigatorService : INavigator
    {
        private readonly Frame _rootFrame;
        private Frame _shellFrame;

        private static readonly Dictionary<string, Type> RootRoutes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { NavigationRoutes.Boot, typeof(UI.Views.BootPage) },
                { NavigationRoutes.Start, typeof(UI.Views.StartPage) },
                { NavigationRoutes.Login, typeof(UI.Views.LoginPage) },
                { NavigationRoutes.AppShell, typeof(MainPage) },
                { NavigationRoutes.Main, typeof(MainPage) },
            };

        private static readonly Dictionary<string, Type> ShellRoutes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
            {
                { NavigationRoutes.Chats, typeof(UI.Views.ChatsPage) },
                { NavigationRoutes.Settings, typeof(UI.Views.SettingsPage) },
                { NavigationRoutes.Debug, typeof(UI.Views.DebugPage) },
            };

        public NavigatorService(Frame frame)
        {
            _rootFrame = frame ?? throw new ArgumentNullException(nameof(frame));
        }

        public bool CanGoBack => _rootFrame?.CanGoBack ?? false;

        public bool CanGoBackInShell => _shellFrame?.CanGoBack ?? false;

        public void AttachShellFrame(object frame)
        {
            _shellFrame = frame as Frame;
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

            // Same page already showing — still clear stack / pass parameter via remount only if needed.
            if (frame.Content != null && frame.Content.GetType() == pageType)
            {
                if (clearStack)
                {
                    ClearFrameBackStack(frame);
                }

                return;
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
