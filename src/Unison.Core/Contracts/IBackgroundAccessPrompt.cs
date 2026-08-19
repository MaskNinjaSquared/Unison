using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Boot-time gate: WhatsApp socket needs OS background-apps permission.
    /// Cancel exits. OK continues only after a fresh check succeeds; otherwise
    /// Settings opens and the same prompt comes back when the app resumes.
    /// </summary>
    public interface IBackgroundAccessPrompt
    {
        Task<bool> EnsureOrExitAsync();
    }
}
