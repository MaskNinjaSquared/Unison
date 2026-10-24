// =============================================================================
// IEndHistoryCatchUpBannerHost
//
// Side effects for EndHistoryCatchUpBannerDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IEndHistoryCatchUpBannerHost
    {
        /// <summary>True when the banner was active and is now cleared.</summary>
        bool TryEndHistoryCatchUpBanner();

        void WriteCatchUpBannerEnd(string reason);

        void FireAndForgetApplyResolvedNamesToChatList();
    }
}
