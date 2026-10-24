// =============================================================================
// IWaitForSessionEstablishedHost
//
// Side effects for WaitForSessionEstablishedDispatch.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IWaitForSessionEstablishedHost
    {
        void LogWaitingForSession(string reason);

        Task WaitSessionEstablishedGateAsync();

        void LogSessionGateAborted(string reason, Exception error);
    }
}
