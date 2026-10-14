// =============================================================================
// RecoveredMessageParticipant
//
// The sender of a message the phone resent as a WebMessageInfo. History envelopes
// often leave MessageKey.participant unset and fill WebMessageInfo.participant
// (field 5); newer builds stash an alt JID in a MessageKey field the generated
// schema does not declare. Protobuf getters answer "" when unset, so the first
// non-blank one wins rather than the first non-null.
//
// Not HistorySyncContentFilter.ResolveParticipant: that one serves the history
// store, answers only for other people's group messages and does not scan alt
// JIDs. The two are kept apart on purpose until the history path is checked.
// =============================================================================
using System;
using Google.Protobuf;
using Proto;

namespace Unison.Core.Helpers
{
    public static class RecoveredMessageParticipant
    {
        /// <returns>The normalized participant, or null when none is set or it names a chat.</returns>
        public static string Resolve(WebMessageInfo info)
        {
            if (info == null)
            {
                return null;
            }

            string raw = FirstNonBlankText.Pick(
                info.Key?.Participant,
                info.Participant,
                ReadAltJid(info.Key));

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string normalized = JidHelper.Normalize(raw);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
                normalized.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalized;
        }

        private static string ReadAltJid(MessageKey key)
        {
            if (key == null)
            {
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = key.ToByteArray();
            }
            catch (Exception)
            {
                // A key that cannot be re-serialized has no alt field to offer; the typed ones already ran.
                return null;
            }

            return MessageKeyAltJidScan.Read(bytes);
        }
    }
}
