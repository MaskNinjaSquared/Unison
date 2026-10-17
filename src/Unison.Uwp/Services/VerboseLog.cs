// =============================================================================
// VerboseLog
//
// The process-wide verbose trace switch behind WhatsAppService.Log and the
// protocol sink. It is a persisted setting, not connection state, so it does not
// live on the client. Static because the log calls it gates are static and run
// before composition.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Constants;
using Unison.Uwp.Helpers;

namespace Unison.Uwp.Services
{
    internal static class VerboseLog
    {
        // Off by default: ~280 trace points sit on the decrypt and history-sync hot paths,
        // and with them on the app crawls on a Lumia.
        private static bool _enabled;
        private static bool _initialized;

        public static bool IsEnabled => _enabled;

        /// <summary>Loads the persisted switch and reports it. Safe to call more than once.</summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                var settings = LocalSettingsAccess.Current;
                bool hasSaved = settings.ContainsKey(LocalSettingsConstants.VerboseLoggingEnabled);
                _enabled = settings.Get<bool>(LocalSettingsConstants.VerboseLoggingEnabled);

                if (!hasSaved)
                {
                    settings.Set(LocalSettingsConstants.VerboseLoggingEnabled, _enabled);
                }

                Debug.WriteLine($"[Logging] Startup verbose logging state: {(_enabled ? "ON" : "OFF")} (persisted={hasSaved})");
            }
            catch (Exception ex)
            {
                _enabled = true;
                Debug.WriteLine($"[Logging] Failed to load verbose logging setting; defaulting ON. Error: {ex.Message}");
            }

            _initialized = true;
        }

        /// <summary>Sets and persists the switch, logging the transition.</summary>
        public static void Set(bool enabled, string source)
        {
            bool previous = _enabled;
            _enabled = enabled;

            try
            {
                LocalSettingsAccess.Current.Set(LocalSettingsConstants.VerboseLoggingEnabled, enabled);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Logging] Failed to persist verbose logging setting: {ex.Message}");
            }

            if (previous != enabled)
            {
                Debug.WriteLine($"[Logging] Verbose logging changed: {(previous ? "ON" : "OFF")} -> {(enabled ? "ON" : "OFF")} (source={source})");
            }
            else
            {
                Debug.WriteLine($"[Logging] Verbose logging unchanged: {(enabled ? "ON" : "OFF")} (source={source})");
            }
        }
    }
}
