// =============================================================================
// CallKeyDecryptor
//
// Opens the <enc> of an inbound <offer>: a Signal pkmsg/msg from the caller's
// device carrying Message{Call{CallKey}}. The callKey seeds every media key
// (E2E-SRTP, WARP), so without it a call can signal but never carry audio.
//
// Decrypting advances the Signal session exactly like a chat message would, so
// it runs once, at offer time, through the same repository.
//
// Ports: meowcaller engine.go decryptInboundCallKey
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Socket.Utils;
using Unison.Socket.WABinary;

namespace Unison.Socket.Notifications
{
    public sealed class CallKeyDecryptor
    {
        private readonly ISignalRepository _signal;
        private readonly ISocketLog _log;

        public CallKeyDecryptor(ISignalRepository signal, ISocketLog log = null)
        {
            _signal = signal ?? throw new ArgumentNullException(nameof(signal));
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="offer">The &lt;offer&gt; action child.</param>
        /// <param name="senderJid">Outer &lt;call from&gt; — the caller's device.</param>
        /// <param name="alternateSenderJid">call-creator, so a PN/LID-keyed session still opens it.</param>
        /// <returns>The raw callKey, or null when absent or unreadable.</returns>
        public async Task<byte[]> TryDecryptAsync(
            BinaryNode offer,
            string senderJid,
            string alternateSenderJid)
        {
            BinaryNode enc = offer?.GetChild("enc");
            byte[] ciphertext = enc?.GetContentBytes();
            string type = enc?.GetAttribute("type");
            if (ciphertext == null || string.IsNullOrEmpty(senderJid) ||
                (type != "pkmsg" && type != "msg"))
            {
                return null;
            }

            string callId = offer.GetAttribute("call-id");
            try
            {
                string decryptionJid = await ResolveDecryptionJidAsync(senderJid).ConfigureAwait(false);
                byte[] plain = await _signal
                    .DecryptMessageAsync(decryptionJid, type, ciphertext, null, alternateSenderJid)
                    .ConfigureAwait(false);
                if (plain == null)
                {
                    _log.Warn("[Call] callKey decrypt returned no data callId=" + callId);
                    return null;
                }

                var message = global::Proto.Message.Parser.ParseFrom(WaPadding.UnpadRandomMax16(plain));
                byte[] key = message.Call?.CallKey?.ToByteArray();
                if (key == null || key.Length == 0)
                {
                    _log.Warn("[Call] offer carried no callKey callId=" + callId);
                    return null;
                }

                _log.Info("[Call] callKey decrypted callId=" + callId + " bytes=" + key.Length);
                return key;
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] callKey decrypt failed callId=" + callId + " type=" + type, ex);
                return null;
            }
        }

        private async Task<string> ResolveDecryptionJidAsync(string sender)
        {
            if (JidUtils.IsAnyLid(sender) || _signal.LidMapping == null)
            {
                return sender;
            }

            try
            {
                string mapped = await _signal.LidMapping.GetLidForPnAsync(sender).ConfigureAwait(false);
                return string.IsNullOrEmpty(mapped) ? sender : mapped;
            }
            catch (Exception ex)
            {
                _log.Warn("[Call] Could not resolve the LID of " + sender, ex);
                return sender;
            }
        }
    }
}
