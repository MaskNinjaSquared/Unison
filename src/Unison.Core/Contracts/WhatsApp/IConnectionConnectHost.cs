using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Early-gate facts <c>ConnectionConnectGates</c> needs before a new transport is opened.
    /// </summary>
    public interface IConnectionConnectGateSource
    {
        bool HasHealthyRegisteredSocket { get; }

        bool HasHealthyUnregisteredSocket { get; }

        /// <summary>
        /// True when a fatal latch blocks connect for a registered (or wiped) session.
        /// Unregistered QR after wipe may still proceed even if a latch was set earlier.
        /// </summary>
        bool ShouldRefuseConnectBecauseSessionDied { get; }
    }
}
