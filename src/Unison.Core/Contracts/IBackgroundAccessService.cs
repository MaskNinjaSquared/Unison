using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// OS permission to run background tasks (UWP <c>BackgroundExecutionManager</c>).
    /// Required before the WhatsApp socket can open (SocketActivityTrigger).
    /// </summary>
    public interface IBackgroundAccessService
    {
        /// <summary>
        /// Re-queries the OS. Call this every time — status after Settings is stale until requested again.
        /// </summary>
        Task<bool> RefreshAllowedAsync();

        /// <summary>Opens the system page where the user can allow this app in the background.</summary>
        Task<bool> OpenSettingsAsync();
    }
}
