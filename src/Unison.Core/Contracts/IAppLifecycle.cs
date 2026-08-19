using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>Process lifetime hooks the UI needs (exit, resume from Settings).</summary>
    public interface IAppLifecycle
    {
        void Exit();

        /// <summary>
        /// Completes after the app leaves the foreground and comes back
        /// (user returning from the system Settings app).
        /// </summary>
        Task WaitUntilForegroundAsync();
    }
}
