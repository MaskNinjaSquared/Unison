// =============================================================================
// GroupJoinParticipantPrep
//
// From group-add rows (participant JID + optional wire phone_number) → LID↔PN
// pairs to file and usync lookup keys. Pure: no I/O.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers.Groups
{
    /// <summary>
    /// Alias pairs and usync targets needed before a GroupJoin system line is labeled.
    /// </summary>
    public sealed class GroupJoinParticipantPrepResult
    {
        public GroupJoinParticipantPrepResult(
            IReadOnlyList<KeyValuePair<string, string>> lidToPn,
            IReadOnlyList<string> usyncLookupJids)
        {
            LidToPn = lidToPn ?? Array.Empty<KeyValuePair<string, string>>();
            UsyncLookupJids = usyncLookupJids ?? Array.Empty<string>();
        }

        /// <summary>LID → PN pairs for <c>RegisterAliasMappings</c>.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> LidToPn { get; }

        /// <summary>PN JIDs (or direct PN participants) to ask usync about.</summary>
        public IReadOnlyList<string> UsyncLookupJids { get; }
    }

    public static class GroupJoinParticipantPrep
    {
        /// <summary>
        /// Builds alias + usync work from participant JID → phone digits (or phone JID) rows.
        /// Empty / blank keys skipped. LID without digits cannot be usync'd.
        /// </summary>
        public static GroupJoinParticipantPrepResult FromParticipantPhones(
            IEnumerable<KeyValuePair<string, string>> participantJidToPhone)
        {
            if (participantJidToPhone == null)
            {
                return new GroupJoinParticipantPrepResult(
                    Array.Empty<KeyValuePair<string, string>>(),
                    Array.Empty<string>());
            }

            var lidToPn = new List<KeyValuePair<string, string>>();
            var lookup = new List<string>();
            var seenLookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, string> row in participantJidToPhone)
            {
                string jid = JidHelper.Normalize(row.Key);
                if (string.IsNullOrEmpty(jid) || JidHelper.IsGroupJid(jid))
                {
                    continue;
                }

                string digits = PhoneNumberHelper.NormalizePhoneDigits(row.Value)
                    ?? PhoneNumberHelper.NormalizePhoneDigits(JidHelper.TryPhoneFromJid(row.Value))
                    ?? JidHelper.TryPhoneFromJid(jid);

                if (!string.IsNullOrEmpty(digits))
                {
                    string pn = digits + "@s.whatsapp.net";
                    if (JidHelper.IsLidJid(jid))
                    {
                        string pairKey = jid + "|" + pn;
                        if (seenPairs.Add(pairKey))
                        {
                            lidToPn.Add(new KeyValuePair<string, string>(jid, pn));
                        }
                    }

                    if (seenLookup.Add(pn))
                    {
                        lookup.Add(pn);
                    }

                    continue;
                }

                // Direct PN/host JID with no separate phone: still ask usync.
                if (!JidHelper.IsLidJid(jid) && seenLookup.Add(jid))
                {
                    lookup.Add(jid);
                }
            }

            return new GroupJoinParticipantPrepResult(lidToPn, lookup);
        }
    }
}
