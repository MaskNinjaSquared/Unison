// =============================================================================
// MessageDecryptor
//
// Opens the encrypted children of a message stanza and fills the envelope.
//
// A stanza can carry several of them - an skmsg for the group content, a pkmsg
// that also opens a session, a plaintext node for newsletters - and each is
// tried in turn. Two behaviours are worth calling out because the current code
// has neither:
//
//   the decryption identity is resolved through the LID mapping, so a session
//   stored under a LID can open a message addressed by phone number;
//
//   and the envelope discloses the sender's other identity, which is stored on
//   the spot - every message that arrives teaches us a mapping for free.
//
// Ports: rc14 decryptMessageNode and getDecryptionJid in
// src/Utils/decode-wa-message.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Socket.Utils;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class MessageDecryptor
    {
        /// <summary>The stanza carried nothing we could decrypt.</summary>
        public const string NoMessageFoundError = "Message absent from node";

        /// <summary>The prekey was used already or never existed. Retrying will not help.</summary>
        public const string MissingKeysError = "Key used already or never filled";

        private readonly ISignalRepository _signal;
        private readonly ISocketLog _log;

        public MessageDecryptor(ISignalRepository signal, ISocketLog log = null)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _signal = signal;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Decrypts in place. Never throws for a message that could not be read: the envelope is
        /// marked as a ciphertext stub instead, and the caller decides whether to ask for a retry.
        /// </summary>
        public async Task DecryptAsync(BinaryNode stanza, MessageEnvelope envelope)
        {
            if (stanza == null || envelope == null)
            {
                return;
            }

            var decryptables = 0;

            foreach (var child in stanza.Children)
            {
                if (child.Tag == "verified_name")
                {
                    ReadVerifiedName(child, envelope);
                    continue;
                }

                if (child.Tag == "unavailable" && child.GetAttribute("type") == "view_once")
                {
                    envelope.Key.IsViewOnce = true;
                    continue;
                }

                if (child.Tag == "enc")
                {
                    int retryCount;
                    if (int.TryParse(child.GetAttribute("count"), out retryCount))
                    {
                        envelope.RetryCount = retryCount;
                    }
                }

                if (child.Tag != "enc" && child.Tag != "plaintext")
                {
                    continue;
                }

                var payload = child.GetContentBytes();
                if (payload == null)
                {
                    continue;
                }

                decryptables++;

                var isPlaintext = child.Tag == "plaintext";
                var e2eType = isPlaintext ? "plaintext" : child.GetAttribute("type");

                var decryptionJid = await ResolveDecryptionJidAsync(envelope.Author).ConfigureAwait(false);

                if (!isPlaintext)
                {
                    await StoreMappingFromEnvelopeAsync(stanza, envelope.Author, decryptionJid).ConfigureAwait(false);
                }

                try
                {
                    var plain = await DecryptPayloadAsync(
                        e2eType,
                        payload,
                        envelope,
                        decryptionJid).ConfigureAwait(false);

                    if (plain == null)
                    {
                        envelope.MarkUndecryptable("Decryption returned no data");
                        continue;
                    }

                    var bytes = isPlaintext ? plain : WaPadding.UnpadRandomMax16(plain);
                    var message = global::Proto.Message.Parser.ParseFrom(bytes);

                    // A message from one of our own devices arrives wrapped, with the real chat
                    // named inside it.
                    if (message.DeviceSentMessage != null && message.DeviceSentMessage.Message != null)
                    {
                        message = message.DeviceSentMessage.Message;
                    }

                    if (message.SenderKeyDistributionMessage != null)
                    {
                        await ProcessSenderKeyAsync(envelope, message).ConfigureAwait(false);
                    }

                    if (envelope.Message == null)
                    {
                        envelope.Message = message;
                    }
                    else
                    {
                        // Several enc children can contribute to one message; later ones win.
                        envelope.Message.MergeFrom(message);
                    }

                    envelope.IsCiphertextStub = false;
                    envelope.StubParameters.Clear();
                }
                catch (Exception ex)
                {
                    _log.Error(
                        "[Decrypt] Failed to decrypt a " + e2eType + " from " + envelope.Author +
                        " in " + envelope.Key.RemoteJid,
                        ex);

                    envelope.MarkUndecryptable(ex.Message);
                }
            }

            if (decryptables == 0 && !envelope.Key.IsViewOnce)
            {
                envelope.MarkUndecryptable(NoMessageFoundError);
            }
        }

        private async Task<byte[]> DecryptPayloadAsync(
            string e2eType,
            byte[] payload,
            MessageEnvelope envelope,
            string decryptionJid)
        {
            switch (e2eType)
            {
                case "plaintext":
                    return payload;

                case "skmsg":
                    return await _signal
                        .DecryptMessageAsync(envelope.Author, e2eType, payload, envelope.Sender)
                        .ConfigureAwait(false);

                case "pkmsg":
                case "msg":
                    // The author is passed as the alternate so a session stored under either
                    // identity can still open the message.
                    return await _signal
                        .DecryptMessageAsync(decryptionJid, e2eType, payload, null, envelope.Author)
                        .ConfigureAwait(false);

                default:
                    throw new InvalidOperationException("Unknown e2e type: " + e2eType);
            }
        }

        /// <summary>
        /// Which identity holds the session. A LID sender is already the answer; a phone number
        /// is looked up, because the session was very likely built under its LID.
        /// </summary>
        private async Task<string> ResolveDecryptionJidAsync(string sender)
        {
            if (JidUtils.IsAnyLid(sender) || _signal.LidMapping == null)
            {
                return sender;
            }

            try
            {
                var mapped = await _signal.LidMapping.GetLidForPnAsync(sender).ConfigureAwait(false);
                return string.IsNullOrEmpty(mapped) ? sender : mapped;
            }
            catch (Exception ex)
            {
                _log.Warn("[Decrypt] Could not resolve the LID of " + sender, ex);
                return sender;
            }
        }

        /// <summary>
        /// Records the LID/PN pair the stanza disclosed. Only when we are decrypting under the
        /// phone number: if we already resolved to a LID, the mapping is known.
        /// </summary>
        private async Task StoreMappingFromEnvelopeAsync(BinaryNode stanza, string sender, string decryptionJid)
        {
            if (_signal.LidMapping == null || decryptionJid != sender)
            {
                return;
            }

            var mapping = AddressingContext.Extract(stanza).ToMapping(sender);
            if (mapping == null)
            {
                return;
            }

            try
            {
                await _signal.LidMapping.StoreMappingsAsync(new[] { mapping }).ConfigureAwait(false);
                _log.Debug("[Decrypt] Learned " + mapping + " from a message envelope");
            }
            catch (Exception ex)
            {
                _log.Warn("[Decrypt] Could not store " + mapping, ex);
            }
        }

        private async Task ProcessSenderKeyAsync(MessageEnvelope envelope, global::Proto.Message message)
        {
            try
            {
                await _signal
                    .ProcessSenderKeyDistributionMessageAsync(envelope.Author, message.SenderKeyDistributionMessage)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The message itself is readable; only future group messages suffer.
                _log.Error("[Decrypt] Failed to store the sender key from " + envelope.Author, ex);
            }
        }

        private void ReadVerifiedName(BinaryNode node, MessageEnvelope envelope)
        {
            var content = node.GetContentBytes();
            if (content == null || content.Length == 0)
            {
                return;
            }

            try
            {
                var certificate = global::Proto.VerifiedNameCertificate.Parser.ParseFrom(content);
                var details = global::Proto.VerifiedNameCertificate.Types.Details.Parser.ParseFrom(certificate.Details);
                envelope.VerifiedBusinessName = details.VerifiedName;
            }
            catch (Exception ex)
            {
                _log.Warn("[Decrypt] Could not read the verified business name", ex);
            }
        }
    }
}
