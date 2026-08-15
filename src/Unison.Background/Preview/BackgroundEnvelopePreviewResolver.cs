using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;

namespace Unison.Background
{
    internal sealed class BackgroundEnvelopePreviewResult
    {
        public BackgroundNotificationContent Notification { get; set; }
        public int MessageEnvelopeCount { get; set; }
        public int NotifiableMessageEnvelopeCount { get; set; }

        public bool HasNotifiableMessageEnvelope
        {
            get { return NotifiableMessageEnvelopeCount > 0; }
        }
    }

    /// <summary>
    /// Fast first-stage preview from the Noise-opened stanza. It can identify the
    /// actual sender before loading protobuf/Signal. The full preview may replace it
    /// moments later with decrypted text.
    /// </summary>
    internal static class BackgroundEnvelopePreviewResolver
    {
        public static async Task<BackgroundNotificationContent>
            ResolveNewestAsync(IList<byte[]> frames)
        {
            BackgroundEnvelopePreviewResult result =
                await ResolveNewestDetailedAsync(frames);
            return result.Notification;
        }

        public static async Task<BackgroundEnvelopePreviewResult>
            ResolveNewestDetailedAsync(IList<byte[]> frames)
        {
            BackgroundDisplayNameSnapshot snapshot =
                await BackgroundDisplayNameStore.LoadSnapshotAsync();
            IDictionary<string, string> names = snapshot.Names;
            var result = new BackgroundEnvelopePreviewResult();

            foreach (byte[] frame in frames ?? new List<byte[]>())
            {
                BinaryNode node;
                try
                {
                    node = BinaryDecoder.Decode(frame);
                }
                catch
                {
                    continue;
                }

                if (node == null ||
                    !string.Equals(
                        node.Tag,
                        "message",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.MessageEnvelopeCount++;
                string from = Attribute(node, "from");
                string participant = FirstNonEmpty(
                    Attribute(node, "participant"),
                    Attribute(node, "sender_lid"),
                    Attribute(node, "sender_pn"));
                string senderJid = FirstNonEmpty(participant, from);
                if (IsOwnSender(senderJid, snapshot) ||
                    IsServerSender(senderJid))
                {
                    continue;
                }

                result.NotifiableMessageEnvelopeCount++;
                bool isGroup = IsGroupJid(from);
                string senderName = FirstNonEmpty(
                    ResolveDisplayName(names, senderJid),
                    Attribute(node, "notify"),
                    Attribute(node, "verified_name"),
                    FriendlyJid(senderJid));
                string chatName = isGroup
                    ? FirstNonEmpty(
                        ResolveDisplayName(names, from),
                        Attribute(node, "subject"),
                        "Grupo do WhatsApp")
                    : FirstNonEmpty(
                        ResolveDisplayName(names, from),
                        senderName,
                        FriendlyJid(from));

                result.Notification =
                    BackgroundPreviewResolver.ResolveRealMessage(
                    from,
                    chatName,
                    senderName,
                    ResolveEnvelopeBody(node),
                    isGroup);
            }

            return result;
        }

        private static string ResolveEnvelopeBody(BinaryNode node)
        {
            string mediaType = Attribute(node, "mediatype");
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                foreach (BinaryNode child in node.GetAllChildren())
                {
                    mediaType = child?.GetAttribute("mediatype");
                    if (!string.IsNullOrWhiteSpace(mediaType))
                        break;
                }
            }

            switch ((mediaType ?? string.Empty).ToLowerInvariant())
            {
                case "image": return "[Image]";
                case "video": return "[Video]";
                case "audio": return "[Audio]";
                case "ptt": return "[Voice Message]";
                case "document": return "[Document]";
                case "sticker": return "[Sticker]";
                default: return "New message";
            }
        }

        private static bool IsOwnSender(
            string senderJid,
            BackgroundDisplayNameSnapshot snapshot)
        {
            string sender = WA.GetBaseJid(
                WA.NormalizeDeviceJid(senderJid));
            string ownPn = WA.GetBaseJid(
                WA.NormalizeDeviceJid(snapshot?.OwnPnJid));
            string ownLid = WA.GetBaseJid(
                WA.NormalizeDeviceJid(snapshot?.OwnLidJid));
            return !string.IsNullOrWhiteSpace(sender) &&
                   (string.Equals(
                        sender,
                        ownPn,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        sender,
                        ownLid,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static string ResolveDisplayName(
            IDictionary<string, string> names,
            string jid)
        {
            if (names == null || string.IsNullOrWhiteSpace(jid))
                return null;
            string normalized = WA.NormalizeDeviceJid(jid);
            string value;
            if (!string.IsNullOrWhiteSpace(normalized) &&
                names.TryGetValue(normalized, out value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            string baseJid = WA.GetBaseJid(normalized);
            return !string.IsNullOrWhiteSpace(baseJid) &&
                   names.TryGetValue(baseJid, out value) &&
                   !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
        }

        private static string FriendlyJid(string jid)
        {
            string baseJid = WA.GetBaseJid(
                WA.NormalizeDeviceJid(jid));
            if (string.IsNullOrWhiteSpace(baseJid))
                return "New message";
            int separator = baseJid.IndexOf('@');
            return separator > 0
                ? baseJid.Substring(0, separator)
                : baseJid;
        }

        private static string Attribute(BinaryNode node, string key)
        {
            string value;
            return node?.Attrs != null &&
                   node.Attrs.TryGetValue(key, out value)
                ? value
                : null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? new string[0])
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static bool IsGroupJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   jid.EndsWith(
                       "@g.us",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsServerSender(string jid)
        {
            return string.Equals(
                       jid,
                       WA.S_WHATSAPP_NET,
                       StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(
                       jid,
                       "@" + WA.S_WHATSAPP_NET,
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
