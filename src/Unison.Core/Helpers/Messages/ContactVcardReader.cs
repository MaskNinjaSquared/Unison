// =============================================================================
// ContactVcardReader
//
// Pulls display name, waid/phone, and optional PHOTO from a WhatsApp ContactMessage
// vCard. Protocol ContactMessage has DisplayName + Vcard only — no dedicated photo
// field; PHOTO lives inside the vCard when the sender included one.
// =============================================================================
using System;
using System.Text;

namespace Unison.Core.Helpers
{
    /// <summary>Parsed facts from a shared-contact envelope / vCard.</summary>
    public sealed class ContactVcardInfo
    {
        public string DisplayName { get; set; }
        public string PhoneDigits { get; set; }
        public string Waid { get; set; }
        public string PhotoBase64 { get; set; }
        public string PhotoMimeType { get; set; }

        /// <summary>PN JID when <see cref="Waid"/> or <see cref="PhoneDigits"/> is known.</summary>
        public string Jid
        {
            get
            {
                string user = !string.IsNullOrWhiteSpace(Waid) ? Waid.Trim() : PhoneDigits;
                if (string.IsNullOrWhiteSpace(user))
                {
                    return null;
                }

                return user + "@s.whatsapp.net";
            }
        }
    }

    /// <summary>Reads WhatsApp-style vCards used by <c>ContactMessage</c>.</summary>
    public static class ContactVcardReader
    {
        /// <summary>
        /// Builds contact card facts from protocol display name + vCard body.
        /// </summary>
        public static ContactVcardInfo Read(string displayName, string vcard)
        {
            var info = new ContactVcardInfo();
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                info.DisplayName = displayName.Trim();
            }

            if (string.IsNullOrWhiteSpace(vcard))
            {
                return info;
            }

            string[] lines = SplitLines(vcard);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                // Unfold continued lines (leading space/tab).
                while (i + 1 < lines.Length &&
                       lines[i + 1].Length > 0 &&
                       (lines[i + 1][0] == ' ' || lines[i + 1][0] == '\t'))
                {
                    line += lines[i + 1].TrimStart();
                    i++;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string keyPart = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string keyUpper = KeyName(keyPart).ToUpperInvariant();

                if (keyUpper == "FN" && string.IsNullOrWhiteSpace(info.DisplayName) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    info.DisplayName = Unescape(value);
                }
                else if (keyUpper == "N" && string.IsNullOrWhiteSpace(info.DisplayName))
                {
                    string fromN = DisplayNameFromN(value);
                    if (!string.IsNullOrWhiteSpace(fromN))
                    {
                        info.DisplayName = fromN;
                    }
                }
                else if (keyUpper == "TEL")
                {
                    ApplyTel(keyPart, value, info);
                }
                else if (keyUpper == "PHOTO")
                {
                    ApplyPhoto(keyPart, value, info);
                }
            }

            if (string.IsNullOrWhiteSpace(info.PhoneDigits) && !string.IsNullOrWhiteSpace(info.Waid))
            {
                info.PhoneDigits = DigitsOnly(info.Waid);
            }

            return info;
        }

        private static void ApplyTel(string keyPart, string value, ContactVcardInfo info)
        {
            string waid = ParamValue(keyPart, "waid");
            if (!string.IsNullOrWhiteSpace(waid))
            {
                string digits = DigitsOnly(waid);
                if (!string.IsNullOrWhiteSpace(digits) && string.IsNullOrWhiteSpace(info.Waid))
                {
                    info.Waid = digits;
                }
            }

            string phone = DigitsOnly(value);
            if (!string.IsNullOrWhiteSpace(phone) && string.IsNullOrWhiteSpace(info.PhoneDigits))
            {
                info.PhoneDigits = phone;
            }
        }

        private static void ApplyPhoto(string keyPart, string value, ContactVcardInfo info)
        {
            if (string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(info.PhotoBase64))
            {
                return;
            }

            string encoding = ParamValue(keyPart, "ENCODING");
            string type = ParamValue(keyPart, "TYPE");
            bool isBase64 =
                string.Equals(encoding, "BASE64", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(encoding, "B", StringComparison.OrdinalIgnoreCase) ||
                keyPart.IndexOf(";BASE64", StringComparison.OrdinalIgnoreCase) >= 0 ||
                LooksLikeBase64(value);

            if (!isBase64)
            {
                return;
            }

            info.PhotoBase64 = value.Replace(" ", string.Empty).Replace("\t", string.Empty);
            if (!string.IsNullOrWhiteSpace(type))
            {
                info.PhotoMimeType = type.IndexOf('/') >= 0
                    ? type
                    : "image/" + type.ToLowerInvariant();
            }
            else
            {
                info.PhotoMimeType = "image/jpeg";
            }
        }

        private static string DisplayNameFromN(string nValue)
        {
            // N: Last;First;Middle;Prefix;Suffix
            string[] parts = (nValue ?? string.Empty).Split(';');
            string last = parts.Length > 0 ? Unescape(parts[0]).Trim() : string.Empty;
            string first = parts.Length > 1 ? Unescape(parts[1]).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(first))
            {
                return last;
            }

            if (string.IsNullOrWhiteSpace(last))
            {
                return first;
            }

            return first + " " + last;
        }

        private static string KeyName(string keyPart)
        {
            // item1.TEL;waid=… → TEL
            string s = keyPart ?? string.Empty;
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1)
            {
                s = s.Substring(dot + 1);
            }

            int semi = s.IndexOf(';');
            if (semi > 0)
            {
                s = s.Substring(0, semi);
            }

            return s.Trim();
        }

        private static string ParamValue(string keyPart, string name)
        {
            if (string.IsNullOrWhiteSpace(keyPart) || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            string[] bits = keyPart.Split(';');
            for (int i = 1; i < bits.Length; i++)
            {
                string bit = bits[i].Trim();
                int eq = bit.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string pname = bit.Substring(0, eq).Trim();
                if (string.Equals(pname, name, StringComparison.OrdinalIgnoreCase))
                {
                    return bit.Substring(eq + 1).Trim().Trim('"');
                }
            }

            return null;
        }

        private static string DigitsOnly(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c >= '0' && c <= '9')
                {
                    sb.Append(c);
                }
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value
                .Replace("\\n", "\n")
                .Replace("\\,", ",")
                .Replace("\\;", ";")
                .Replace("\\\\", "\\");
        }

        private static string[] SplitLines(string vcard)
        {
            return vcard
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.None);
        }

        private static bool LooksLikeBase64(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 16)
            {
                return false;
            }

            int sample = Math.Min(value.Length, 64);
            for (int i = 0; i < sample; i++)
            {
                char c = value[i];
                if (c == ' ' || c == '\t')
                {
                    continue;
                }

                if (!((c >= 'A' && c <= 'Z') ||
                      (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') ||
                      c == '+' || c == '/' || c == '='))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
