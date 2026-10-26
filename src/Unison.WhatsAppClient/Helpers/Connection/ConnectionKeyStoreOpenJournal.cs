// =============================================================================
// ConnectionKeyStoreOpenJournal
//
// Journal detail after open applies the persistent key store (fast-resume vs cold).
// =============================================================================
namespace Unison.WhatsAppClient.Helpers
{
    public static class ConnectionKeyStoreOpenJournal
    {
        public static string EventName(bool reuseLoadedKeyState) =>
            reuseLoadedKeyState ? "fast-resume-key-store-reused" : "key-store-cold-loaded";

        public static string Detail(int sessions, int prekeys) =>
            "sessions=" + sessions + "; prekeys=" + prekeys;
    }
}
