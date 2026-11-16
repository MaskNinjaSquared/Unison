// =============================================================================
// SessionEstablishedGate
//
// Completes when WA login success / session-initialized lands for the current
// socket. Renewed per connect; cancelled (then renewed) when the session is
// cleared so pending EnsureConnected waiters stop.
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class SessionEstablishedGate
    {
        private TaskCompletionSource<bool> _tcs = Create();

        public Task Task => Volatile.Read(ref _tcs).Task;

        public void Signal() => Volatile.Read(ref _tcs).TrySetResult(true);

        public void Renew() => Volatile.Write(ref _tcs, Create());

        public void CancelAndRenew()
        {
            Volatile.Read(ref _tcs).TrySetCanceled();
            Renew();
        }

        private static TaskCompletionSource<bool> Create() =>
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
