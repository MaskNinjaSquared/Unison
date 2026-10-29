// =============================================================================
// StartupLaunchPresentation
//
// "Start with Windows" toggle rules. Windows owns the final state: once the
// user turns the task off in Task Manager, RequestEnableAsync cannot turn it
// back on, and policy states cannot be changed by the app at all.
// =============================================================================
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public static class StartupLaunchPresentation
    {
        public const string DisabledByUserHintKey = "Settings_StartupLaunchDisabledByUser";
        public const string PolicyHintKey = "Settings_StartupLaunchPolicy";

        public static bool IsOn(StartupLaunchState state) =>
            state == StartupLaunchState.Enabled || state == StartupLaunchState.EnabledByPolicy;

        public static bool CanToggle(StartupLaunchState state) =>
            state == StartupLaunchState.Enabled || state == StartupLaunchState.Disabled;

        /// <summary>Resource key explaining why the toggle is locked, or null.</summary>
        public static string HintKey(StartupLaunchState state)
        {
            switch (state)
            {
                case StartupLaunchState.DisabledByUser:
                    return DisabledByUserHintKey;
                case StartupLaunchState.DisabledByPolicy:
                case StartupLaunchState.EnabledByPolicy:
                    return PolicyHintKey;
                default:
                    return null;
            }
        }
    }
}
