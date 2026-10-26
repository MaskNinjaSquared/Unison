// =============================================================================
// IPublishConnectionUpdateHost
//
// Side effects for PublishConnectionUpdateDispatch.
// =============================================================================

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IPublishConnectionUpdateHost
    {
        string CurrentConnectionStatus { get; set; }

        bool ShouldClearReconnectRequiredToast(string status);

        void SetSuppressReconnectToast(bool suppress);

        void ClearReconnectRequiredToast(string status);

        void StampLastConnectionEventUtc();

        void WriteConnectionStatusJournal(string status);

        void LogConnectionStatus(string status);

        void RaiseConnectionUpdate(string status);
    }
}
