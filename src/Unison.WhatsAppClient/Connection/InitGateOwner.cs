// =============================================================================
// InitGateOwner
//
// Owns the InitializeConnectionStateAsync SemaphoreSlim that used to live on
// WhatsAppService. One cold-start connection-state init at a time.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class InitGateOwner
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public Task EnterAsync() => _gate.WaitAsync();

        public void Exit() => _gate.Release();
    }
}
