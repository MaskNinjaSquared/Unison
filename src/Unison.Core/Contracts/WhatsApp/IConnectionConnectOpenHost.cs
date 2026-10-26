using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionConnectOpen</c> needs after prepare: assign the session the
    /// factory built, apply key-store side effects, refuse/abort, and start health.
    /// Socket construction and WinRT transport open live on
    /// <see cref="IConnectionSocketSessionFactory"/>.
    /// </summary>
    public interface IConnectionConnectOpenHost
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
