using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Unison.Desktop.Tray
{
    /// <summary>
    /// Hide / restore the UWP frame window from the full-trust stub (close-to-tray).
    /// CoreWindow lives in the app process; the chrome is <c>ApplicationFrameWindow</c> —
    /// walk to the root ancestor when we have a CoreWindow hwnd.
    /// </summary>
    internal static class NativeWindow
    {
        private const int SwHide = 0;
        private const int SwShow = 5;
        private const int SwRestore = 9;
        private const uint GaRoot = 2;

        private static readonly List<IntPtr> HiddenRoots = new List<IntPtr>();

        public static void HideAppWindow(long coreWindowId, int processId)
        {
            IntPtr root = ResolveRoot(coreWindowId, processId);
            if (root == IntPtr.Zero)
            {
                return;
            }

            if (!IsWindowVisible(root))
            {
                return;
            }

            if (!HiddenRoots.Contains(root))
            {
                HiddenRoots.Add(root);
            }

            ShowWindow(root, SwHide);
        }

        public static void ShowAppWindow(long coreWindowId, int processId)
        {
            var roots = new List<IntPtr>(HiddenRoots);
            HiddenRoots.Clear();

            IntPtr resolved = ResolveRoot(coreWindowId, processId);
            if (resolved != IntPtr.Zero && !roots.Contains(resolved))
            {
                roots.Add(resolved);
            }

            foreach (IntPtr root in roots)
            {
                if (root == IntPtr.Zero || !IsWindow(root))
                {
                    continue;
                }

                ShowWindow(root, SwRestore);
                ShowWindow(root, SwShow);
                SetForegroundWindow(root);
            }
        }

        private static IntPtr ResolveRoot(long coreWindowId, int processId)
        {
            if (coreWindowId != 0)
            {
                var core = new IntPtr(coreWindowId);
                if (IsWindow(core))
                {
                    IntPtr root = GetAncestor(core, GaRoot);
                    return root != IntPtr.Zero ? root : core;
                }
            }

            return FindRootByProcessId(processId);
        }

        private static IntPtr FindRootByProcessId(int processId)
        {
            if (processId <= 0)
            {
                return IntPtr.Zero;
            }

            IntPtr found = IntPtr.Zero;
            EnumWindows((hwnd, lParam) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if ((int)pid != processId || !IsWindowVisible(hwnd))
                {
                    return true;
                }

                var className = new StringBuilder(64);
                GetClassName(hwnd, className, className.Capacity);
                string name = className.ToString();
                // Prefer the frame; fall back to any visible top-level owned by the app.
                if (name == "ApplicationFrameWindow" || name == "Windows.UI.Core.CoreWindow")
                {
                    IntPtr root = GetAncestor(hwnd, GaRoot);
                    found = root != IntPtr.Zero ? root : hwnd;
                    return false;
                }

                return true;
            }, IntPtr.Zero);

            return found;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
