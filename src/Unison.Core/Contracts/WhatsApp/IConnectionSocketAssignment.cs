// =============================================================================
// IConnectionSocketAssignment
//
// What the portable connect-open host needs from the (still UWP) owner of the
// live socket field: build the session request, assign/clear the field, apply
// key-store side effects, refuse/abort, start health. Pump and reconnect stay
// on the compatibility client until an explicit Phase D follow-up.
// =============================================================================
using Unison.Core.Contracts;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IConnectionSocketAssignment
    {
        ConnectionSocketSessionRequest CreateSocketSessionRequest(bool reuseLoadedKeyState);

        void AssignSocketSession(object session);

        void ApplyPersistentKeyStore(object keyStore, bool reuseLoadedKeyState);

        bool ShouldRefuseConnectBecauseSessionDied { get; }

        void AbortAssignedSocket(string journalReason);

        void StartHealthMonitorIfAssignedCurrent();

        void ClearAssignedSocketAfterFailedConnect();
    }
}
