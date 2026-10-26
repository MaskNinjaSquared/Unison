using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionConnectPrepare</c> needs after early Connect gates and before
    /// a new transport is built.
    /// </summary>
    public interface IConnectionConnectPrepareHost
    {
        /// <summary>Clears session-established / QR / enrichment latches for a fresh attempt.</summary>
        void ResetPerConnectionGates();

        void MarkConnecting();

        void StopHealthAndDropCurrentSocket();

        void CancelDeferredAvatars();

        Task EnsureAuthLoadedAsync();

        bool HasAuthState { get; }

        bool ShouldRefuseConnectBecauseSessionDied { get; }

        /// <summary>
        /// Replay-drain flag, suppress clear, pre-session-close policy, history request reset.
        /// </summary>
        void ApplyRegisteredSessionConnectState();
    }
}
