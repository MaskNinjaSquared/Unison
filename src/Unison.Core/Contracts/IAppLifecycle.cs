using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>Process lifetime hooks the UI needs (exit, resume from Settings).</summary>
    public interface IAppLifecycle
    {
        void Exit();

        /// <summary>
        /// Completes after the app leaves the foreground and comes back
        /// (Settings, People contact card, or any system UI that deactivates the window).
        /// </summary>
        Task WaitUntilForegroundAsync();
    }
}
