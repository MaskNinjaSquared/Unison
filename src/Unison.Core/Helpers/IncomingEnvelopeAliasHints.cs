// =============================================================================
// IncomingEnvelopeAliasHints
//
// PN/LID pairs an inbound envelope may carry before usync answers. The pump
// still registers them; this only decides which pairs are well-formed.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    public sealed class IncomingAliasHint
    {
        public IncomingAliasHint(string lidJid, string pnJid, string source)
        {
            LidJid = lidJid;
            PnJid = pnJid;
            Source = source ?? string.Empty;
        }

        public string LidJid { get; }
        public string PnJid { get; }
        public string Source { get; }
    }

    public static class IncomingEnvelopeAliasHints
    {
        /// <summary>
        /// Collects alias pairs from envelope identity fields. JIDs are normalized by
        /// <paramref name="normalize"/> when provided.
        /// </summary>
        public static IReadOnlyList<IncomingAliasHint> Collect(
            string senderLid,
            string fromJid,
            string peerRecipientPn,
            string peerRecipientLid,
            string recipientJid,
            string participant,
            string participantAlt,
            Func<string, string> normalize = null)
        {
            var hints = new List<IncomingAliasHint>(4);
            Func<string, string> norm = normalize ?? (s => JidHelper.Normalize(s));

            if (!string.IsNullOrEmpty(senderLid) && JidHelper.IsPhoneJid(fromJid))
            {
                hints.Add(new IncomingAliasHint(norm(senderLid), norm(fromJid), "sender_lid"));
            }

            if (!string.IsNullOrEmpty(peerRecipientPn) && JidHelper.IsLidJid(fromJid))
            {
                hints.Add(new IncomingAliasHint(norm(fromJid), norm(peerRecipientPn), "peer_recipient_pn"));
            }

            if (!string.IsNullOrEmpty(peerRecipientLid) && JidHelper.IsPhoneJid(recipientJid))
            {
                hints.Add(new IncomingAliasHint(norm(peerRecipientLid), norm(recipientJid), "peer_recipient_lid"));
            }

            if (!string.IsNullOrEmpty(participant) && !string.IsNullOrEmpty(participantAlt))
            {
                string p = norm(participant);
                string alt = norm(participantAlt);
                if (!string.IsNullOrEmpty(p) && !string.IsNullOrEmpty(alt))
                {
                    if (p.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                    {
                        hints.Add(new IncomingAliasHint(p, alt, "group-participant-alt"));
                    }
                    else if (alt.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
                    {
                        hints.Add(new IncomingAliasHint(alt, p, "group-participant-alt"));
                    }
                }
            }

            return hints;
        }
    }
}
