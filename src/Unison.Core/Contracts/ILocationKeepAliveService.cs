using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// LocationTracking + Geolocator keep-alive (Unogram/tdlib pattern) so Mobile
    /// can delay/avoid freezing the process while the session is allowed.
    /// Position is never used — subscription only justifies ExtendedExecution.
    /// </summary>
    public interface ILocationKeepAliveService
    {
        /// <summary>True while an ExtendedExecution LocationTracking session is held.</summary>
        bool IsActive { get; }

        /// <summary>Start Geolocator + LocationTracking session. Returns false if denied.</summary>
        Task<bool> StartAsync();

        /// <summary>Release session and geolocator.</summary>
        void Stop();

        /// <summary>Start if <see cref="Constants.LocalSettingsConstants.LocationKeepAliveEnabled"/>,
        /// otherwise stop.
        /// </summary>
        Task ApplyConfigAsync();

        /// <summary>Stop and do not restart (process exit).</summary>
        void Shutdown();
    }
}
