using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.System.Profile;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// "Start with Windows" through the package startup task declared in the manifest
    /// (<c>uap5:StartupTask</c>, TaskId <see cref="TaskId"/>). Desktop only: Mobile and builds
    /// before 1709 report <see cref="StartupLaunchState.NotSupported"/>.
    /// </summary>
    public sealed class StartupLaunchService : IStartupLaunchService
    {
        /// <summary>Must match <c>uap5:StartupTask TaskId</c> in Package.appxmanifest.</summary>
        public const string TaskId = "UnisonStartup";

        private const string DesktopFamily = "Windows.Desktop";

        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Lazy<bool> _isSupported;

        public StartupLaunchService(IRuntimeDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
            _isSupported = new Lazy<bool>(DetectSupport);
        }

        public bool IsSupported => _isSupported.Value;

        public async Task<StartupLaunchState> GetStateAsync()
        {
            // Stay on the caller's context: Settings binds from the UI thread.
            StartupTask task = await TryGetTaskAsync();
            return task == null ? StartupLaunchState.NotSupported : Map(task.State);
        }

        public async Task<StartupLaunchState> SetEnabledAsync(bool enabled)
        {
            // RequestEnableAsync shows a system consent UI — must stay on the UI thread.
            StartupTask task = await TryGetTaskAsync();
            if (task == null)
            {
                return StartupLaunchState.NotSupported;
            }

            StartupLaunchState before = Map(task.State);
            if (enabled && (before == StartupLaunchState.Enabled || before == StartupLaunchState.EnabledByPolicy))
            {
                return before;
            }

            if (!enabled && before == StartupLaunchState.Disabled)
            {
                return before;
            }

            // Task Manager / policy: RequestEnableAsync throws 0x8007139F (ERROR_INVALID_STATE).
            if (enabled &&
                (before == StartupLaunchState.DisabledByUser || before == StartupLaunchState.DisabledByPolicy))
            {
                return before;
            }

            StartupLaunchState result;
            try
            {
                if (enabled)
                {
                    result = Map(await task.RequestEnableAsync());
                }
                else
                {
                    task.Disable();
                    result = Map(task.State);
                }
            }
            catch (Exception ex)
            {
                _diagnostics?.RecordException("startup-task", "set-failed", ex, "enabled=" + enabled + "; before=" + before);
                return Map(task.State);
            }

            _diagnostics?.Write(
                "startup-task",
                "set",
                "requested=" + enabled +
                "; before=" + before +
                "; state=" + result);
            return result;
        }

        private async Task<StartupTask> TryGetTaskAsync()
        {
            if (!IsSupported)
            {
                return null;
            }

            try
            {
                return await StartupTask.GetAsync(TaskId);
            }
            catch (Exception ex)
            {
                // Missing manifest declaration (e.g. a package built without the extension).
                _diagnostics?.RecordException("startup-task", "get-failed", ex);
                return null;
            }
        }

        private static bool DetectSupport()
        {
            try
            {
                return string.Equals(
                           AnalyticsInfo.VersionInfo.DeviceFamily,
                           DesktopFamily,
                           StringComparison.OrdinalIgnoreCase) &&
                       ApiInformation.IsTypePresent("Windows.ApplicationModel.StartupTask");
            }
            catch
            {
                return false;
            }
        }

        private static StartupLaunchState Map(StartupTaskState state)
        {
            switch (state)
            {
                case StartupTaskState.Enabled:
                    return StartupLaunchState.Enabled;
                case StartupTaskState.DisabledByUser:
                    return StartupLaunchState.DisabledByUser;
                case StartupTaskState.DisabledByPolicy:
                    return StartupLaunchState.DisabledByPolicy;
                case StartupTaskState.EnabledByPolicy:
                    return StartupLaunchState.EnabledByPolicy;
                default:
                    return StartupLaunchState.Disabled;
            }
        }
    }
}
