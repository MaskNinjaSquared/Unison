using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.ApplicationModel.Background;
using Windows.System;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// UWP background-apps permission. Must <see cref="RefreshAllowedAsync"/> after
    /// returning from Settings — <c>GetAccessStatus</c> stays stale until then.
    /// </summary>
    public sealed class BackgroundAccessService : IBackgroundAccessService
    {
        private const string PrivacyBackgroundAppsUri = "ms-settings:privacy-backgroundapps";
        private const string BatterySaverUri = "ms-settings:batterysaver";

        private BackgroundAccessStatus _status = BackgroundAccessStatus.Unspecified;

        public async Task<bool> RefreshAllowedAsync()
        {
            try
            {
                _status = await BackgroundExecutionManager.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BackgroundAccess] RequestAccessAsync: " + ex.Message);
                return false;
            }

            RuntimeDiagnosticsService.Instance.Write(
                "background-access",
                "refresh",
                "status=" + _status);

            return !IsDenied(_status);
        }

        public async Task<bool> OpenSettingsAsync()
        {
            string uri = IsSystemPolicyDenied(_status)
                ? BatterySaverUri
                : PrivacyBackgroundAppsUri;

            try
            {
                return await Launcher.LaunchUriAsync(new Uri(uri));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[BackgroundAccess] OpenSettings: " + ex.Message);
                return false;
            }
        }

        internal static bool IsDenied(BackgroundAccessStatus status)
        {
            if (status == BackgroundAccessStatus.DeniedByUser ||
                status == BackgroundAccessStatus.DeniedBySystemPolicy)
            {
                return true;
            }

#pragma warning disable CS0618
            if (status == BackgroundAccessStatus.Denied)
            {
                return true;
            }
#pragma warning restore CS0618

            return false;
        }

        private static bool IsSystemPolicyDenied(BackgroundAccessStatus status)
        {
            return status == BackgroundAccessStatus.DeniedBySystemPolicy;
        }
    }
}
