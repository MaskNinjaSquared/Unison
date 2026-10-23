namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Localized strings the chat-list strip needs (self fallback + "Yesterday").
    /// Keeps WinRT resource lookups out of WhatsAppClient helpers.
    /// </summary>
    public static class LocalizedChatStripLabels
    {
        public static string SelfFallbackName() =>
            LocalizedStrings.Get("Chat_SelfFallbackName", "You");

        public static string Yesterday() =>
            LocalizedStrings.Get("Common_Yesterday", "Yesterday");
    }
}
