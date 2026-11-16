// =============================================================================
// IConnectionSessionHost
//
// Session wipe / logout / memory / reconnect-suppress the ConnectionFacade
// needs without naming the concrete client.
// =============================================================================
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IConnectionSessionHost
    {
        Task InitializeConnectionStateAsync();

        Task ClearSessionAsync();

        Task NotifyServerLogoutAsync(string reason = null);

        Task ReleaseMemoryAsync();

        void SuppressReconnectFromPolicy(string reason);
    }
}