// =============================================================================
// LidMapping
//
// One account seen from both address spaces: its phone-number JID and its LID.
//
// The pair travels together everywhere - usync replies, history sync, group
// metadata, app state - so it gets a type rather than a tuple, and the two ends
// are named so no call site has to guess which string is which.
//
// Ports: rc14 LIDMapping in src/Types/Auth.ts
// =============================================================================
namespace Unison.Socket.Signal
{
    public sealed class LidMapping
    {
        public LidMapping()
        {
        }

        public LidMapping(string lid, string pn)
        {
            Lid = lid;
            Pn = pn;
        }

        /// <summary>The identity in LID space, e.g. "123456789@lid" or "123456789:12@lid".</summary>
        public string Lid { get; set; }

        /// <summary>The identity in phone-number space, e.g. "5511999999999@s.whatsapp.net".</summary>
        public string Pn { get; set; }

        public override string ToString()
        {
            return Pn + " <-> " + Lid;
        }
    }
}
