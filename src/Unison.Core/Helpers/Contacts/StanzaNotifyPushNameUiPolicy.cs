// =============================================================================
// StanzaNotifyPushNameUiPolicy
//
// Every inbound stanza carries notify=. History floods used to schedule a UI
// hop per message even when the push name did not change, stacking Normal
// dispatcher work behind urgent live-strip on Mobile Low (ApplyStanzaNotify
// held the UI for tens of seconds while roster/tip waited).
// =============================================================================
namespace Unison.Core.Helpers.Contacts
{
    public static class StanzaNotifyPushNameUiPolicy
    {
        /// <summary>
        /// Placeholder chat.Name rewrite is only useful when the captured push
        /// name actually changed.
        /// </summary>
        public static bool ShouldSchedulePlaceholderNameUi(bool pushNameChanged) =>
            pushNameChanged;
    }
}
