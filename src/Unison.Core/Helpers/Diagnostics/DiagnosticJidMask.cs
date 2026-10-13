// =============================================================================
// DiagnosticJidMask
//
// A JID written into the runtime journal. The tail is enough to follow one chat
// across events; the rest of the number does not belong in a log.
// =============================================================================
namespace Unison.Core.Helpers
{
    public static class DiagnosticJidMask
    {
        public static string Apply(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return "-";
            }

            int at = jid.IndexOf('@');
            string user = at > 0 ? jid.Substring(0, at) : jid;
            string domain = at > 0 ? jid.Substring(at) : string.Empty;
            string tail = user.Length <= 4 ? user : user.Substring(user.Length - 4);
            return "*" + tail + domain;
        }
    }
}
