// =============================================================================
// WaitForSessionEstablishedOwner
//
// Owns IWaitForSessionEstablishedHost. The session-established gate is
// recreated per connection by the host, so it is read through a port.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class WaitForSessionEstablishedOwner : IWaitForSessionEstablishedHost
    {
        private readonly Func<Task> _sessionEstablishedGate;
        private readonly Action<string> _log;

        public WaitForSessionEstablishedOwner(
            Func<Task> sessionEstablishedGate,
            Action<string> log)
        {
            _sessionEstablishedGate = sessionEstablishedGate
                ?? throw new ArgumentNullException(nameof(sessionEstablishedGate));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task WaitAsync(string reason) => WaitForSessionEstablishedDispatch.RunAsync(this, reason);

        void IWaitForSessionEstablishedHost.LogWaitingForSession(string reason) =>
            _log("Waiting for session establishment before processing " + reason + ".");

        Task IWaitForSessionEstablishedHost.WaitSessionEstablishedGateAsync() => _sessionEstablishedGate();

        void IWaitForSessionEstablishedHost.LogSessionGateAborted(string reason, Exception error) =>
            _log(
                "Startup gate completed abnormally while waiting to process " + reason +
                "; aborting gated work. " + error.Message);
    }
}
