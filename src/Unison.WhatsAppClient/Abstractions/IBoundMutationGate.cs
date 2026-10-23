// =============================================================================
// IBoundMutationGate
//
// PropertyChanged-safe mutations for timeline bubbles (revoke tombstone,
// reaction apply, outgoing status). Inline when no UI dispatcher is attached.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IBoundMutationGate
    {
        Task RunAsync(Action action);
    }
}
