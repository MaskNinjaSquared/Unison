// =============================================================================
// SsrcDerivation
//
// Relay stream SSRC derivation (HKDF-SHA256 over call id + participant LID) and
// the device-qualified LID helpers used as E2E-SRTP HKDF info.
// Ports: meowcaller rtp/ssrc.go (MIT)
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Unison.Voip.Engine.Crypto;

namespace Unison.Voip.Engine.Rtp
{
    public static class SsrcDerivation
    {
        public const int WasmRelayStreamCount = 9;

        /// <summary>Relay stream slot for a participant's video SSRC (audio is slot 0).</summary>
        public const uint VideoSlotWord = 2;

        /// <summary>Relay stream slot for RTC app-data such as call reactions.</summary>
        public const uint AppDataSlotWord = 6;

        /// <summary>Local hop-by-hop FEC stream slots advertised in multi-party SFU mode.</summary>
        public const uint HBHFECTXSlotWord = 7;
        public const uint HBHFECRXSlotWord = 8;

        private static readonly uint[] SlotWords = { 0, 1, 4, 2, 3, 5, 7, 8, 6 };

        /// <summary>Slot words for the 9-stream relay allocate plan (fresh copy).</summary>
        public static uint[] WasmRelayStreamSlotWords
        {
            get { return (uint[])SlotWords.Clone(); }
        }

        /// <summary>
        /// HKDF-SHA256(salt = slotWord LE32, ikm = callId, info = lid, 4), read back as LE u32.
        /// </summary>
        public static uint DeriveWasmParticipantSsrc(string callId, string lid, uint slotWord)
        {
            if (callId == null)
            {
                throw new ArgumentNullException(nameof(callId));
            }
            if (lid == null)
            {
                throw new ArgumentNullException(nameof(lid));
            }

            byte[] salt =
            {
                (byte)slotWord,
                (byte)(slotWord >> 8),
                (byte)(slotWord >> 16),
                (byte)(slotWord >> 24)
            };
            byte[] okm = Hkdf.Sha256(salt, Encoding.UTF8.GetBytes(callId), Encoding.UTF8.GetBytes(lid), 4);
            return (uint)(okm[0] | (okm[1] << 8) | (okm[2] << 16) | (okm[3] << 24));
        }

        /// <summary>All 9 relay-stream SSRCs in <see cref="WasmRelayStreamSlotWords"/> order.</summary>
        public static uint[] DeriveWasmRelayStreamSsrcs(string callId, string lid)
        {
            var output = new uint[WasmRelayStreamCount];
            for (int i = 0; i < SlotWords.Length; i++)
            {
                output[i] = DeriveWasmParticipantSsrc(callId, lid, SlotWords[i]);
            }
            return output;
        }

        /// <summary>
        /// Strips the resource, gives a bare @lid an implicit :0 device suffix, and
        /// passes everything else through unchanged.
        /// </summary>
        public static string FormatE2ESrtpParticipantID(string jid)
        {
            string bare = BareJid(jid);
            int at = bare.LastIndexOf('@');
            if (at <= 0)
            {
                return bare;
            }

            string user = bare.Substring(0, at);
            string domain = bare.Substring(at + 1);
            if (domain == "lid" && user.IndexOf(':') < 0)
            {
                return user + ":0@" + domain;
            }
            return bare;
        }

        /// <summary>
        /// Device-qualified LID variants the recv path tries as HKDF info, deduplicated
        /// in insertion order.
        /// </summary>
        public static string[] E2EParticipantIDVariants(string jid)
        {
            var output = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            Action<string> push = s =>
            {
                string t = s.Trim();
                if (t.Length != 0 && seen.Add(t))
                {
                    output.Add(t);
                }
            };

            string bare = BareJid(jid);
            push(bare);
            push(FormatE2ESrtpParticipantID(jid));

            int at = bare.LastIndexOf('@');
            if (at > 0)
            {
                string user = bare.Substring(0, at);
                string domain = bare.Substring(at + 1);
                int colon = user.IndexOf(':');
                if (domain == "lid" && colon >= 0)
                {
                    string baseUser = user.Substring(0, colon);
                    push(baseUser + ":0@" + domain);
                    push(baseUser + "@" + domain);
                }
            }
            return output.ToArray();
        }

        private static string BareJid(string jid)
        {
            string value = jid ?? string.Empty;
            int slash = value.IndexOf('/');
            if (slash >= 0)
            {
                value = value.Substring(0, slash);
            }
            return value.Trim();
        }
    }
}
