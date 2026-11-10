// =============================================================================
// StanzaShape
//
// A one-line outline of an outgoing message stanza: its attributes, the order
// of its children, and who the participant copies are addressed to. A refused
// send comes back as a bare error code, so this outline is what is left to
// compare against what rc14 would have built.
//
// Ciphertext is reduced to byte counts; nothing here can be decrypted.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Baileys.Protocol;

namespace Unison.Socket.Messages
{
    public static class StanzaShape
    {
        private const int MaxRepeatsListed = 5;
        private const string LidSuffix = "@lid";

        public static string Describe(BinaryNode stanza)
        {
            if (stanza == null)
            {
                return "(no stanza)";
            }

            var attrs = string.Join(
                " ",
                stanza.Attrs.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value));
            var children = string.Join(",", stanza.Children.Select(c => c.Tag));

            var targets = stanza.GetChild("participants")?.GetChildren("to") ?? new List<BinaryNode>();
            var jids = targets.Select(t => t.GetAttribute("jid") ?? string.Empty).ToList();
            int lid = jids.Count(j => j.EndsWith(LidSuffix, StringComparison.OrdinalIgnoreCase));
            int pkmsg = targets.Count(t => t.GetChild("enc")?.GetAttribute("type") == "pkmsg");
            var repeated = jids
                .GroupBy(j => j, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            string outline = attrs +
                " children=" + children +
                " targets=" + jids.Count +
                " lid=" + lid +
                " pn=" + (jids.Count - lid) +
                " pkmsg=" + pkmsg +
                " bytes=" + PayloadBytes(stanza);

            if (repeated.Count > 0)
            {
                outline += " repeated=" + string.Join(",", repeated.Take(MaxRepeatsListed)) +
                    (repeated.Count > MaxRepeatsListed ? "(+" + (repeated.Count - MaxRepeatsListed) + ")" : string.Empty);
            }

            return outline;
        }

        private static long PayloadBytes(BinaryNode node)
        {
            var bytes = node.Content as byte[];
            if (bytes != null)
            {
                return bytes.Length;
            }

            return node.Children.Sum(child => PayloadBytes(child));
        }
    }
}
