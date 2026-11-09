// =============================================================================
// ISessionOpenOnDemandPendingConsumeHost
//
// Side effects for SessionOpenOnDemandPendingConsumeDispatch.
// =============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface ISessionOpenOnDemandPendingConsumeHost
    {
        bool IsPairingBootstrapActive { get; }

        int PeekSessionOpenOnDemandPendingCount();

        IReadOnlyList<string> TakeSessionOpenOnDemandPending();

        void LogSessionOpenOnDemandPending(string message);

        void ClaimSessionOpenOnDemand(string jid);

        Task EnsureHistoryOnDemandAsync(string jid);
    }
}
