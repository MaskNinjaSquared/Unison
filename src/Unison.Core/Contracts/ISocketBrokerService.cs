using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Platform socket-broker (UWP SocketActivityTrigger). Other hosts can no-op.
    /// </summary>
    public interface ISocketBrokerService
    {
        Guid TaskId { get; }
        bool IsReady { get; }

        Task<bool> EnsureReadyAsync();
        Task<bool> RecreateRegistrationAsync(string reason);
        Task DisposeBrokerSocketAsync(string socketId = null);
    }
}
