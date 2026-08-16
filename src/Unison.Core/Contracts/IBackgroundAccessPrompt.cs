using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Boot-time gate: WhatsApp socket needs OS background-apps permission.
    /// Cancel exits the process; OK re-checks before opening Settings.
    /// </summary>
    public interface IBackgroundAccessPrompt
    {
        Task<bool> EnsureOrExitAsync();
    }
}
