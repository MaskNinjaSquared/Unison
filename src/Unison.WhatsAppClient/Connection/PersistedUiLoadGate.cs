// =============================================================================
// PersistedUiLoadGate
//
// Owns the LoadPersistedUiState SemaphoreSlim that used to live on
// WhatsAppService. One persisted-UI catalogue load at a time.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class PersistedUiLoadGate
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public Task EnterAsync() => _gate.WaitAsync();

        public void Exit() => _gate.Release();
    }
}
