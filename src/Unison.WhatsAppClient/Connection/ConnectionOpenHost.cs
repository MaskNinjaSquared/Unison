// =============================================================================
// ConnectionOpenHost
//
// Portable IConnectionConnectOpenHost. Session build/wire/transport stay on
// IConnectionSocketSessionFactory (UWP); assignment + health/refuse stay on
// IConnectionSocketAssignment (UWP compatibility client). Pump / reconnect are
// not part of this host.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Connection
{
    /// <summary>Portable connect-open host — no WhatsAppService type in the signature.</summary>
    public sealed class ConnectionOpenHost : IConnectionConnectOpenHost
    {
        private readonly IConnectionSocketAssignment _assignment;

        public ConnectionOpenHost(IConnectionSocketAssignment assignment)
        {
            _assignment = assignment ?? throw new ArgumentNullException(nameof(assignment));
        }

        public ConnectionSocketSessionRequest CreateSocketSessionRequest(bool reuseLoadedKeyState) =>
            _assignment.CreateSocketSessionRequest(reuseLoadedKeyState);

        public void AssignSocketSession(object session) =>
            _assignment.AssignSocketSession(session);

        public void ApplyPersistentKeyStore(object keyStore, bool reuseLoadedKeyState) =>
            _assignment.ApplyPersistentKeyStore(keyStore, reuseLoadedKeyState);

        public bool ShouldRefuseConnectBecauseSessionDied =>
            _assignment.ShouldRefuseConnectBecauseSessionDied;

        public void AbortAssignedSocket(string journalReason) =>
            _assignment.AbortAssignedSocket(journalReason);

        public void StartHealthMonitorIfAssignedCurrent() =>
            _assignment.StartHealthMonitorIfAssignedCurrent();

        public void ClearAssignedSocketAfterFailedConnect() =>
            _assignment.ClearAssignedSocketAfterFailedConnect();
    }
}
