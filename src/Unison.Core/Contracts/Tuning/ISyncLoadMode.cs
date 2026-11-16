// =============================================================================
// ISyncLoadMode
//
// Whether the WhatsApp client should do its work in the light (sliced, Low-priority)
// shape right now. The product layer decides; the client only reads the answer.
// =============================================================================

namespace Unison.Core.Contracts.Tuning
{
    public interface ISyncLoadMode
    {
        bool PreferFrugal { get; }
    }
}
