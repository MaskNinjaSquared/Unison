using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Proto;
using Unison.Baileys.Protocol;
using Unison.Baileys.Client;

namespace Unison.Background
{
    internal sealed class BackgroundPreviewReplayResult
    {
        public IList<BackgroundNotificationContent> Notifications { get; set; }
        public int ReplayedEntries { get; set; }
        public int ReplayedFrames { get; set; }
        public int DecodedMessageNodes { get; set; }
        public int CurrentSequenceMessageNodes { get; set; }
        public bool TimedOut { get; set; }
    }

    /// <summary>
    /// Replays pending decoded Noise frames over an isolated copy of Signal state.
    /// SignalHandler is intentionally constructed without an IKeyStore: direct and
    /// group ratchets may advance in memory for preview continuity, but the task can
    /// never persist those speculative changes.
    /// </summary>
    internal static class BackgroundMessagePreviewEngine
    {
        public static async Task<BackgroundPreviewReplayResult>
            ReplayForSequenceAsync(
                IList<BrokerJournalPendingEntry> pendingEntries,
                ulong notificationSequence,
                DateTime deadlineUtc)
        {
            var result = new BackgroundPreviewReplayResult
            {
                Notifications =
                    new List<BackgroundNotificationContent>()
            };

            AuthState authState =
                await BackgroundSignalSnapshotStore.LoadCloneAsync();
            if (authState == null)
            {
                return result;
            }
            IDictionary<string, string> displayNames =
                await BackgroundDisplayNameStore.LoadAsync();

            var signal = new SignalHandler(authState, null);
            foreach (BrokerJournalPendingEntry entry in
                     (pendingEntries ??
                      new List<BrokerJournalPendingEntry>())
                     .OrderBy(item => item.Sequence))
            {
                if (DateTime.UtcNow >= deadlineUtc)
                {
                    result.TimedOut = true;
                    break;
                }

                BrokerDecodedFrameBatch batch;
                if (!BrokerDecodedFrameEnvelope.TryUnpack(
                        entry.Payload,
                        out batch))
                {
                    continue;
                }

                result.ReplayedEntries++;
                foreach (byte[] frame in batch.Frames ??
                         new List<byte[]>())
                {
                    if (DateTime.UtcNow >= deadlineUtc)
                    {
                        result.TimedOut = true;
                        break;
                    }

                    result.ReplayedFrames++;
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

                    result.DecodedMessageNodes++;
                    if (entry.Sequence == notificationSequence)
                    {
                        result.CurrentSequenceMessageNodes++;
                    }
                    BackgroundNotificationContent content =
                        TryProcessMessageNode(
                            node,
                            authState,
                            signal,
                            displayNames);
                    if (entry.Sequence == notificationSequence &&
                        content != null &&
                        content.IsRealMessage)
                    {
                        result.Notifications.Add(content);
                    }
                }
            }

            return result;
        }

        private static BackgroundNotificationContent TryProcessMessageNode(
            BinaryNode node,
            AuthState authState,
            SignalHandler signal,
            IDictionary<string, string> displayNames)
        {
            string from = Attribute(node, "from");
            string participant = Attribute(node, "participant");
            string addressingMode = Attribute(node, "addressing_mode");
            string participantPn = Attribute(node, "participant_pn");
            string participantLid = Attribute(node, "participant_lid");
            string senderPn = Attribute(node, "sender_pn");
            string senderLid = Attribute(node, "sender_lid");
            string peerPn = Attribute(node, "peer_recipient_pn");
            string peerLid = Attribute(node, "peer_recipient_lid");

            if (string.IsNullOrWhiteSpace(participant) &&
                IsGroupJid(from))
            {
                participant = string.Equals(
                    addressingMode,
                    "lid",
                    StringComparison.OrdinalIgnoreCase)
                    ? FirstNonEmpty(senderLid, participantLid)
                    : FirstNonEmpty(senderPn, participantPn);
            }

            string participantAlt = string.Equals(
                addressingMode,
                "lid",
                StringComparison.OrdinalIgnoreCase)
                ? FirstNonEmpty(participantPn, senderPn, peerPn)
                : FirstNonEmpty(participantLid, senderLid, peerLid);
            string author = FirstNonEmpty(
                participant,
                senderLid,
                senderPn,
                from);

            BackgroundNotificationContent latest = null;
            foreach (BinaryNode child in
                     GetDecryptableChildren(node, authState))
            {
                if (!(child.Content is byte[] encryptedData))
                    continue;

                Message message = null;
                bool fromOwnLinkedDevice = false;
                try
                {
                    if (string.Equals(
                        child.Tag,
                        "plaintext",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        message = Message.Parser.ParseFrom(encryptedData);
                    }
                    else
                    {
                        string signalType = child.GetAttribute("type");
                        byte[] decrypted = signal.DecryptMessage(
                            encryptedData,
                            author,
                            signalType,
                            from,
                            participantAlt);
                        if (decrypted == null)
                            continue;
                        message = Message.Parser.ParseFrom(
                            UnpadRandomMax16(decrypted));
                    }

                    if (message?.DeviceSentMessage?.Message != null)
                    {
                        string destination =
                            message.DeviceSentMessage.DestinationJid;
                        if (!IsGroupJid(from) &&
                            !string.IsNullOrWhiteSpace(destination))
                        {
                            from = destination;
                        }
                        message = message.DeviceSentMessage.Message;
                        fromOwnLinkedDevice = true;
                    }

                    if (message?.SenderKeyDistributionMessage != null)
                    {
                        signal.ProcessSenderKeyDistribution(
                            author,
                            message.SenderKeyDistributionMessage,
                            participantAlt);
                    }

                    string preview = ExtractPreview(message);
                    if (string.IsNullOrWhiteSpace(preview))
                        continue;
                    if (fromOwnLinkedDevice ||
                        IsOwnSender(from, participant, authState))
                    {
                        continue;
                    }

                    bool isGroup = IsGroupJid(from);
                    string senderName = FirstNonEmpty(
                        ResolveDisplayName(displayNames, author),
                        ResolveDisplayName(displayNames, participantAlt),
                        Attribute(node, "notify"),
                        Attribute(node, "verified_name"),
                        FriendlyJid(author));
                    string chatName = isGroup
                        ? FirstNonEmpty(
                            ResolveDisplayName(displayNames, from),
                            Attribute(node, "subject"),
                            "Grupo do WhatsApp")
                        : FirstNonEmpty(
                            ResolveDisplayName(displayNames, from),
                            senderName,
                            FriendlyJid(from));

                    latest = BackgroundPreviewResolver.ResolveRealMessage(
                        from,
                        chatName,
                        senderName,
                        preview,
                        isGroup);
                }
                catch
                {
                    // Try another enc candidate (for example a participants/to
                    // fanout entry). Failed Signal attempts commit no state.
                }
            }
            return latest;
        }

        private static IEnumerable<BinaryNode> GetDecryptableChildren(
            BinaryNode node,
            AuthState authState)
        {
            var direct = new List<BinaryNode>();
            var targeted = new List<BinaryNode>();
            foreach (BinaryNode child in node.GetAllChildren())
            {
                if (child == null) continue;
                if (child.Tag == "enc" || child.Tag == "plaintext")
                {
                    direct.Add(child);
                }
            }

            BinaryNode participants = node.GetChild("participants");
            if (participants != null)
            {
                foreach (BinaryNode to in participants.GetChildren("to"))
                {
                    if (!IsExactOwnDevice(
                            to.GetAttribute("jid"),
                            authState))
                    {
                        continue;
                    }

                    foreach (BinaryNode candidate in to.GetAllChildren())
                    {
                        if (candidate.Tag == "enc" ||
                            candidate.Tag == "plaintext")
                        {
                            targeted.Add(candidate);
                        }
                    }
                }
            }

            if (IsGroupJid(Attribute(node, "from")) &&
                targeted.Count > 0)
            {
                var ordered = new List<BinaryNode>(
                    targeted.Count + direct.Count);
                ordered.AddRange(targeted);
                ordered.AddRange(direct.Where(
                    child => string.Equals(
                        child.GetAttribute("type"),
                        "skmsg",
                        StringComparison.OrdinalIgnoreCase)));
                ordered.AddRange(direct.Where(
                    child => !string.Equals(
                        child.GetAttribute("type"),
                        "skmsg",
                        StringComparison.OrdinalIgnoreCase)));
                return ordered;
            }

            direct.AddRange(targeted);
            return direct;
        }

        private static bool IsExactOwnDevice(
            string jid,
            AuthState authState)
        {
            string normalized = WA.NormalizeDeviceJid(jid);
            string ownPn = WA.NormalizeDeviceJid(
                authState?.Me?.Id);
            string ownLid = WA.NormalizeDeviceJid(
                authState?.Me?.Lid);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (string.Equals(
                        normalized,
                        ownPn,
                        StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(ownLid) &&
                     string.Equals(
                         normalized,
                         ownLid,
                         StringComparison.OrdinalIgnoreCase)));
        }

        private static string ExtractPreview(Message message)
        {
            Message value = Unwrap(message);
            if (value == null) return null;
            if (!string.IsNullOrWhiteSpace(value.Conversation))
                return value.Conversation;
            if (!string.IsNullOrWhiteSpace(
                    value.ExtendedTextMessage?.Text))
                return value.ExtendedTextMessage.Text;
            if (value.ImageMessage != null)
                return WithCaption(
                    "[Foto]",
                    value.ImageMessage.Caption);
            if (value.VideoMessage != null)
                return WithCaption(
                    "[Vídeo]",
                    value.VideoMessage.Caption);
            if (value.DocumentMessage != null)
                return WithCaption(
                    "[Documento]",
                    value.DocumentMessage.FileName);
            if (value.AudioMessage != null)
                return value.AudioMessage.Ptt
                    ? "[Mensagem de voz]"
                    : "[Áudio]";
            if (value.StickerMessage != null)
                return "[Figurinha]";
            if (value.ContactMessage != null)
                return WithCaption(
                    "[Contato]",
                    value.ContactMessage.DisplayName);
            if (value.ContactsArrayMessage != null)
                return "[Contatos]";
            if (value.LocationMessage != null ||
                value.LiveLocationMessage != null)
                return "[Localização]";
            if (value.ReactionMessage != null)
                return WithCaption(
                    "[Reação]",
                    value.ReactionMessage.Text);
            if (value.PollCreationMessage != null)
                return WithCaption(
                    "[Enquete]",
                    value.PollCreationMessage.Name);
            if (value.PollCreationMessageV2 != null)
                return WithCaption(
                    "[Enquete]",
                    value.PollCreationMessageV2.Name);
            if (value.PollCreationMessageV3 != null)
                return WithCaption(
                    "[Enquete]",
                    value.PollCreationMessageV3.Name);
            if (value.ScheduledCallCreationMessage != null)
                return WithCaption(
                    "[Chamada agendada]",
                    value.ScheduledCallCreationMessage.Title);
            if (value.CallLogMesssage != null || value.Call != null)
                return "[Chamada]";
            if (value.ProtocolMessage != null)
                return null;
            if (value.SenderKeyDistributionMessage != null)
                return null;
            return "[Nova mensagem]";
        }

        private static Message Unwrap(Message message)
        {
            Message current = message;
            while (current != null)
            {
                if (current.ViewOnceMessage?.Message != null)
                {
                    current = current.ViewOnceMessage.Message;
                    continue;
                }
                if (current.ViewOnceMessageV2?.Message != null)
                {
                    current = current.ViewOnceMessageV2.Message;
                    continue;
                }
                if (current.EphemeralMessage?.Message != null)
                {
                    current = current.EphemeralMessage.Message;
                    continue;
                }
                if (current.DocumentWithCaptionMessage?.Message != null)
                {
                    current =
                        current.DocumentWithCaptionMessage.Message;
                    continue;
                }
                if (current.EditedMessage?.Message != null)
                {
                    current = current.EditedMessage.Message;
                    continue;
                }
                break;
            }
            return current;
        }

        private static byte[] UnpadRandomMax16(byte[] data)
        {
            if (data == null || data.Length == 0)
                throw new InvalidOperationException("Empty Signal payload");
            byte paddingLength = data[data.Length - 1];
            if (paddingLength > 16 || paddingLength > data.Length)
                return data;
            var result = new byte[data.Length - paddingLength];
            Buffer.BlockCopy(data, 0, result, 0, result.Length);
            return result;
        }

        private static bool IsOwnSender(
            string from,
            string participant,
            AuthState state)
        {
            string sender = FirstNonEmpty(participant, from);
            string normalized = WA.GetBaseJid(
                WA.NormalizeDeviceJid(sender));
            string ownPn = WA.GetBaseJid(
                WA.NormalizeDeviceJid(state?.Me?.Id));
            string ownLid = WA.GetBaseJid(
                WA.NormalizeDeviceJid(state?.Me?.Lid));
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (string.Equals(
                        normalized,
                        ownPn,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        normalized,
                        ownLid,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static string FriendlyJid(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
                return "Nova mensagem";
            string baseJid = WA.GetBaseJid(
                WA.NormalizeDeviceJid(jid));
            int separator = (baseJid ?? string.Empty).IndexOf('@');
            return separator > 0
                ? baseJid.Substring(0, separator)
                : baseJid;
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
            if (!string.IsNullOrWhiteSpace(baseJid) &&
                names.TryGetValue(baseJid, out value) &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return null;
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

        private static string WithCaption(
            string label,
            string caption)
        {
            return string.IsNullOrWhiteSpace(caption)
                ? label
                : label + " " + caption.Trim();
        }

        private static bool IsGroupJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   jid.EndsWith(
                       "@g.us",
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
