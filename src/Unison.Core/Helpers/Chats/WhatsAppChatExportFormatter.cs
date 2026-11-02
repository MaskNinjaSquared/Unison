// =============================================================================
// WhatsAppChatExportFormatter
//
// Line shape matching WhatsApp Desktop/Android export TXT.
// =============================================================================
using System;
using System.Globalization;
using System.Text;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class WhatsAppChatExportFormatter
    {
        public const string HiddenMediaPlaceholder = "<Mídia oculta>";

        public static string BuildBaseFileName(string chatDisplayName)
        {
            string name = SanitizeFileName(
                string.IsNullOrWhiteSpace(chatDisplayName) ? "Chat" : chatDisplayName.Trim());
            return "Conversa do WhatsApp com " + name;
        }

        public static string FormatEncryptionHeader(DateTime firstMessageLocal, string headerBody)
        {
            string stamp = firstMessageLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            string body = string.IsNullOrWhiteSpace(headerBody)
                ? "As mensagens e ligações são protegidas com a criptografia de ponta a ponta. Somente as pessoas que fazem parte da conversa podem ler, ouvir e compartilhar esse conteúdo. *Saiba mais*"
                : headerBody.Trim();
            return stamp + " - " + body;
        }

        public static string FormatMessageLine(
            HistoryMessage row,
            string authorLabel,
            bool includeMedia,
            out string attachedVcfFileName)
        {
            attachedVcfFileName = null;
            if (row == null)
            {
                return null;
            }

            DateTime local = (row.TimestampUtc ?? row.UpdatedAtUtc).ToLocalTime();
            string stamp = local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
            string author = string.IsNullOrWhiteSpace(authorLabel) ? "Unknown" : authorLabel.Trim();
            string body = FormatBody(row, includeMedia, out attachedVcfFileName);
            if (string.IsNullOrEmpty(body))
            {
                body = string.Empty;
            }

            return stamp + " - " + author + ": " + body;
        }

        public static string FormatBody(
            HistoryMessage row,
            bool includeMedia,
            out string attachedVcfFileName)
        {
            attachedVcfFileName = null;
            if (row == null)
            {
                return string.Empty;
            }

            if (row.IsRevoked || row.Kind == ChatPreviewKind.Revoked)
            {
                return "Esta mensagem foi apagada";
            }

            if (row.Kind == ChatPreviewKind.Contact)
            {
                string display = !string.IsNullOrWhiteSpace(row.MediaFileName)
                    ? row.MediaFileName.Trim()
                    : (!string.IsNullOrWhiteSpace(row.Body) ? row.Body.Trim() : "contato");
                attachedVcfFileName = SanitizeFileName(display) + ".vcf";
                // WhatsApp prefixes a left-to-right mark before the file name.
                return "\u200E" + attachedVcfFileName + " (arquivo anexado)";
            }

            if (IsMediaKind(row.Kind))
            {
                if (includeMedia && !string.IsNullOrWhiteSpace(row.MediaFileName))
                {
                    return "\u200E" + row.MediaFileName.Trim() + " (arquivo anexado)";
                }

                return HiddenMediaPlaceholder;
            }

            return row.Body ?? string.Empty;
        }

        public static string BuildVcf(HistoryMessage contactRow)
        {
            if (contactRow == null)
            {
                return null;
            }

            string fn = !string.IsNullOrWhiteSpace(contactRow.MediaFileName)
                ? contactRow.MediaFileName.Trim()
                : (!string.IsNullOrWhiteSpace(contactRow.Body) ? contactRow.Body.Trim() : "Contact");
            string phone = contactRow.MediaDirectPath;
            string jid = contactRow.MediaUrl;
            string waid = null;
            if (!string.IsNullOrWhiteSpace(jid))
            {
                int at = jid.IndexOf('@');
                waid = at > 0 ? jid.Substring(0, at) : jid;
            }
            else if (!string.IsNullOrWhiteSpace(phone))
            {
                waid = DigitsOnly(phone);
            }

            var sb = new StringBuilder();
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            sb.AppendLine("N:;" + EscapeVcard(fn) + ";;;");
            sb.AppendLine("FN:" + EscapeVcard(fn));
            if (!string.IsNullOrWhiteSpace(waid))
            {
                string tel = FormatTel(phone, waid);
                sb.AppendLine("item1.TEL;waid=" + DigitsOnly(waid) + ":" + tel);
                sb.AppendLine("item1.X-ABLabel:Celular");
            }

            sb.AppendLine("END:VCARD");
            return sb.ToString();
        }

        public static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Chat";
            }

            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                if (c < 32 || c == '<' || c == '>' || c == ':' || c == '"' ||
                    c == '/' || c == '\\' || c == '|' || c == '?' || c == '*')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            string cleaned = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(cleaned) ? "Chat" : cleaned;
        }

        private static bool IsMediaKind(ChatPreviewKind kind)
        {
            return kind == ChatPreviewKind.Image ||
                   kind == ChatPreviewKind.Video ||
                   kind == ChatPreviewKind.Gif ||
                   kind == ChatPreviewKind.Sticker ||
                   kind == ChatPreviewKind.Voice ||
                   kind == ChatPreviewKind.Document;
        }

        private static string DigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9')
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string FormatTel(string phone, string waid)
        {
            if (!string.IsNullOrWhiteSpace(phone) && phone.Trim().StartsWith("+", StringComparison.Ordinal))
            {
                return phone.Trim();
            }

            string digits = DigitsOnly(!string.IsNullOrWhiteSpace(phone) ? phone : waid);
            return string.IsNullOrEmpty(digits) ? string.Empty : "+" + digits;
        }

        private static string EscapeVcard(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace(";", "\\;")
                .Replace(",", "\\,")
                .Replace("\n", "\\n");
        }
    }
}
