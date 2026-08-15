// =============================================================================
// MediaRetryNode
//
// Reads the phone's answer to a media re-upload request.
//
// The answer is either an error code or a small encrypted blob holding the new
// location of the file. Decryption needs the original message's media key, which
// only the host has, so this stops at parsing.
//
// Ports: rc14 decodeMediaRetryNode in src/Utils/messages-media.ts
// =============================================================================
using Unison.Baileys.Protocol;
using Unison.Socket.Messages;
using Unison.Socket.Models;

namespace Unison.Socket.Notifications
{
    public static class MediaRetryNode
    {
        public static MediaRetryUpdate Decode(BinaryNode node)
        {
            if (node == null)
            {
                return null;
            }

            var rmr = node.GetChild("rmr");
            if (rmr == null)
            {
                return null;
            }

            var update = new MediaRetryUpdate
            {
                Key = new MessageEnvelopeKey
                {
                    Id = node.GetAttribute("id"),
                    RemoteJid = rmr.GetAttribute("jid"),
                    FromMe = rmr.GetAttribute("from_me") == "true",
                    Participant = rmr.GetAttribute("participant")
                }
            };

            var error = node.GetChild("error");
            if (error != null)
            {
                int code;
                update.ErrorCode = int.TryParse(error.GetAttribute("code"), out code) ? code : -1;
                return update;
            }

            var encrypt = node.GetChild("encrypt");
            if (encrypt == null)
            {
                return update;
            }

            var payload = encrypt.GetChild("enc_p");
            var iv = encrypt.GetChild("enc_iv");
            if (payload == null || iv == null)
            {
                return update;
            }

            update.Media = new MediaRetryCiphertext
            {
                Ciphertext = payload.GetContentBytes(),
                Iv = iv.GetContentBytes()
            };

            return update;
        }
    }
}
