using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What <c>ConnectionConnectOpen</c> needs to build, wire, and open the transport
    /// after <c>ConnectionConnectPrepare</c> succeeds.
    /// </summary>
    public interface IConnectionConnectOpenHost
    {
        void BuildAssignAndWireSocket(bool reuseLoadedKeyState);

        Task InitializeKeyStoreAfterAssignAsync(bool reuseLoadedKeyState);

        bool ShouldRefuseConnectBecauseSessionDied { get; }

        void AbortAssignedSocket(string journalReason);

        Task ConnectAssignedTransportAsync();

        void StartHealthMonitorIfAssignedCurrent();

        void ClearAssignedSocketAfterFailedConnect();
    }
}
