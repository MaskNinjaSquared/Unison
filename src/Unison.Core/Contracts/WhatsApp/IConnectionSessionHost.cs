// =============================================================================
// IConnectionSessionHost
//
// Session wipe / logout / memory / reconnect-suppress the ConnectionFacade
// needs without naming IWhatsAppService.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
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