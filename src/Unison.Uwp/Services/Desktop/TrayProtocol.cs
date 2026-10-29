// =============================================================================
// TrayProtocol
//
// AppService contract between the UWP app and the tray stub — same shape as
// Unigram's org.telegram.bridge (ValueSet keys, ParameterGroup ids). Compiled
// into both sides (linked into Unison.Uwp); the tray never references Core.
// =============================================================================
namespace Unison.Desktop.Tray
{
    public static class TrayProtocol
    {
        /// <summary>Must match <c>uap:AppService Name</c> in the desktop package manifest.</summary>
        public const string ServiceName = "org.unison.bridge";

        /// <summary>Must match <c>desktop:ParameterGroup GroupId</c> for the tray.</summary>
        public const string SystemTrayGroup = "SystemTrayGroup";

        /// <summary>Must match <c>desktop:ParameterGroup GroupId</c> for CheckNetIsolation.</summary>
        public const string LoopbackExemptGroup = "LoopbackExemptGroup";

        public const string ArgSystemTray = "/SystemTray";
        public const string ArgLoopbackExempt = "/LoopbackExempt";

        // --- UWP → Stub (Unigram keys) ---
        public const string KeyProcessId = "ProcessId";
        public const string KeyOpenText = "OpenText";
        public const string KeyExitText = "ExitText";
        public const string KeyUnreadCount = "UnreadCount";
        public const string KeyUnreadUnmutedCount = "UnreadUnmutedCount";
        public const string KeyCloseRequested = "CloseRequested";
        /// <summary>CoreWindow hwnd (int64) so the stub can hide the ApplicationFrameWindow.</summary>
        public const string KeyWindowId = "WindowId";

        // --- Stub → UWP ---
        public const string KeyExit = "Exit";

        public const string ApplicationId = "App";
    }
}
