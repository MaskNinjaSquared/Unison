// =============================================================================
// IPendingMessageFlushHost
//
// What PendingMessageQueue.Add decides next (idle timer vs flush-now). The
// façade owns the queue; the client still owns the timer and the SQLite drain.
// =============================================================================
using Unison.Core.State;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPendingMessageFlushHost
    {
        void ApplyFlushAction(PendingFlushAction action);
    }
}
