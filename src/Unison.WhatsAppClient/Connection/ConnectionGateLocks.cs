// =============================================================================
// ConnectionGateLocks
//
// Owns the connect and resume/ensure SemaphoreSlim gates that used to live on
// WhatsAppService. ConnectAsync enters Connect; ConnectionEnsure shares Resume
// so resume and send do not race two connects.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class ConnectionGateLocks
    {
        private readonly SemaphoreSlim _connect = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _resume = new SemaphoreSlim(1, 1);

        /// <summary>Gate for <c>ConnectAsync</c> (one transport open at a time).</summary>
        public Task EnterConnectAsync() => _connect.WaitAsync();

        public void ExitConnect() => _connect.Release();

        /// <summary>
        /// Gate passed to <c>ConnectionEnsure</c> so resume and outbound ensure share
        /// one connect budget.
        /// </summary>
        public SemaphoreSlim ResumeGate => _resume;
    }
}
