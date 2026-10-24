// =============================================================================
// IBeginHistoryCatchUpBannerHost
//
// Side effects for BeginHistoryCatchUpBannerDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IBeginHistoryCatchUpBannerHost
    {
        void LatchHistoryCatchUpBanner();

        void NoteFullHistoryCatchUpProgress(string reason);

        void WriteCatchUpBannerBegin(string reason);
    }
}
