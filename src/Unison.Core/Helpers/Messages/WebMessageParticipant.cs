// =============================================================================
// WebMessageParticipant
//
// The sender written on a WebMessageInfo, for a message the phone resent and for
// the history store alike. Envelopes often leave MessageKey.participant unset and
// fill WebMessageInfo.participant (field 5); newer builds stash an alt JID in a
// MessageKey field the generated schema does not declare. Protobuf getters answer
// "" when unset, so the first non-blank one wins rather than the first non-null.
//
// The alt scan skips only field 4, so a direct envelope with no participant
// answers its own remoteJid. HistorySyncContentFilter.ResolveParticipant adds the
// history store's gates on top: group only, never own, never the chat itself.
// =============================================================================
using System;
using Google.Protobuf;
using Proto;

namespace Unison.Core.Helpers
{
    public static class WebMessageParticipant
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
