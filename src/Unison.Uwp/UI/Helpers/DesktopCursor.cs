using System;
using System.Collections.Generic;
using Unison.Core.Contracts;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Attached property for PC mouse cursors on tap targets (hand) and non-interactive locks (blocked).
    /// Uses <see cref="CoreWindow.PointerCursor"/> — no WinUI Input package required.
    /// No-op on phone; touch has no pointer.
    /// </summary>
    public static class DesktopCursor
    {
        public static readonly DependencyProperty CursorKindProperty =
            DependencyProperty.RegisterAttached(
                "CursorKind",
                typeof(DesktopCursorKind),
                typeof(DesktopCursor),
                new PropertyMetadata(DesktopCursorKind.Default, OnCursorKindChanged));

        private static readonly Dictionary<UIElement, DesktopCursorKind> Hooked =
            new Dictionary<UIElement, DesktopCursorKind>();

        private static int _handRefs;
        private static int _blockedRefs;
        private static CoreCursor _savedCursor;

        public static DesktopCursorKind GetCursorKind(DependencyObject obj) =>
            (DesktopCursorKind)obj.GetValue(CursorKindProperty);

        public static void SetCursorKind(DependencyObject obj, DesktopCursorKind value) =>
            obj.SetValue(CursorKindProperty, value);

        private static void OnCursorKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            Unhook(element);

            if (!IsDesktop())
            {
                return;
            }

            var kind = (DesktopCursorKind)e.NewValue;
            if (kind == DesktopCursorKind.Default)
            {
                return;
            }

            Hooked[element] = kind;
            element.PointerEntered += Element_PointerEntered;
            element.PointerExited += Element_PointerExited;
        }

        private static void Element_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement element && Hooked.TryGetValue(element, out DesktopCursorKind kind))
            {
                Enter(kind);
            }
        }

        private static void Element_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is UIElement element && Hooked.TryGetValue(element, out DesktopCursorKind kind))
            {
                Leave(kind);
            }
        }

        private static void Enter(DesktopCursorKind kind)
        {
            CoreWindow window = Window.Current?.CoreWindow;
            if (window == null)
            {
                return;
            }

            if (kind == DesktopCursorKind.Blocked)
            {
                if (_blockedRefs == 0 && _handRefs == 0)
                {
                    _savedCursor = window.PointerCursor;
                }

                _blockedRefs++;
                window.PointerCursor = new CoreCursor(CoreCursorType.UniversalNo, 1);
                return;
            }

            if (kind == DesktopCursorKind.Hand)
            {
                if (_blockedRefs == 0)
                {
                    if (_handRefs == 0)
                    {
                        _savedCursor = window.PointerCursor;
                    }

                    _handRefs++;
                    window.PointerCursor = new CoreCursor(CoreCursorType.Hand, 1);
                }
                else
                {
                    _handRefs++;
                }
            }
        }

        private static void Leave(DesktopCursorKind kind)
        {
            CoreWindow window = Window.Current?.CoreWindow;
            if (window == null)
            {
                return;
            }

            if (kind == DesktopCursorKind.Blocked)
            {
                _blockedRefs = Math.Max(0, _blockedRefs - 1);
            }
            else if (kind == DesktopCursorKind.Hand)
            {
                _handRefs = Math.Max(0, _handRefs - 1);
            }

            UpdateCursor(window);
        }

        private static void UpdateCursor(CoreWindow window)
        {
            if (_blockedRefs > 0)
            {
                window.PointerCursor = new CoreCursor(CoreCursorType.UniversalNo, 1);
                return;
            }

            if (_handRefs > 0)
            {
                window.PointerCursor = new CoreCursor(CoreCursorType.Hand, 1);
                return;
            }

            window.PointerCursor = _savedCursor ?? new CoreCursor(CoreCursorType.Arrow, 1);
            _savedCursor = null;
        }

        private static void Unhook(UIElement element)
        {
            if (!Hooked.Remove(element))
            {
                return;
            }

            element.PointerEntered -= Element_PointerEntered;
            element.PointerExited -= Element_PointerExited;
        }

        private static bool IsDesktop()
        {
            try
            {
                var info = App.Services?.GetService(typeof(ISystemInfoProvider)) as ISystemInfoProvider;
                return info == null || !info.IsMobile();
            }
            catch
            {
                return true;
            }
        }
    }
}
