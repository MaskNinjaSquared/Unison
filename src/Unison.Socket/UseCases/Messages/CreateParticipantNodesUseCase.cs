// =============================================================================
// CreateParticipantNodesUseCase
//
// Encrypts one message once per recipient device and wraps each copy in a
// <to><enc> pair.
//
// Two things decide what a device receives. Our own other devices get the
// device-sent-message form, which carries the real recipient so the phone can
// file the message in the right chat - but the device we are sending from does
// not, because it already has it. And if any copy comes out as a pkmsg, the
// stanza must carry our signed device identity, which is why that flag travels
// back with the nodes rather than being worked out again by the caller.
//
// Ports: rc14 createParticipantNodes in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class ParticipantNodesResult
    {
        public ParticipantNodesResult()
        {
            Nodes = new List<BinaryNode>();
        }

        public IList<BinaryNode> Nodes { get; private set; }

        /// <summary>True when at least one copy opened a new session and needs proof of identity.</summary>
        public bool ShouldIncludeDeviceIdentity { get; set; }
    }

    public sealed class CreateParticipantNodesUseCase
    {
        private readonly ISignalRepository _signal;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly ISocketLog _log;

        public CreateParticipantNodesUseCase(
            ISignalRepository signal,
            Func<string> meId,
            Func<string> meLid,
            ISocketLog log = null)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _signal = signal;
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Last chance to alter the message per recipient, for features like disappearing
        /// messages that rewrite the payload before it is sealed.
        /// </summary>
        public Func<global::Proto.Message, string, Task<global::Proto.Message>> PatchMessageBeforeSending { get; set; }

        /// <param name="extraAttrs">Extra attributes for every enc node, such as mediatype.</param>
        /// <param name="deviceSentMessage">
        /// The wrapped form to send to our own other devices. Null when the message is not one
        /// we are sending, such as a sender key distribution.
        /// </param>
        public async Task<ParticipantNodesResult> ExecuteAsync(
            IList<string> recipientJids,
            global::Proto.Message message,
            IDictionary<string, string> extraAttrs = null,
            global::Proto.Message deviceSentMessage = null)
        {
            var result = new ParticipantNodesResult();
            if (recipientJids == null || recipientJids.Count == 0 || message == null)
            {
                return result;
            }

            var meId = _meId();
            var meLid = _meLid();
            var ownPnUser = JidUtils.GetUser(meId);
            var ownLidUser = JidUtils.GetUser(meLid);

            var failures = 0;

            foreach (var jid in recipientJids)
            {
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }

                try
                {
                    var payload = SelectPayload(jid, message, deviceSentMessage, meId, meLid, ownPnUser, ownLidUser);

                    if (PatchMessageBeforeSending != null)
                    {
                        payload = await PatchMessageBeforeSending(payload, jid).ConfigureAwait(false) ?? payload;
                    }

                    var encrypted = await _signal
                        .EncryptMessageAsync(jid, payload.ToByteArray())
                        .ConfigureAwait(false);

                    if (encrypted == null || encrypted.Ciphertext == null)
                    {
                        failures++;
                        continue;
                    }

                    if (encrypted.IsPreKeyMessage)
                    {
                        result.ShouldIncludeDeviceIdentity = true;
                    }

                    var attrs = new Dictionary<string, string> { { "v", "2" }, { "type", encrypted.Type } };
                    if (extraAttrs != null)
                    {
                        foreach (var attr in extraAttrs)
                        {
                            attrs[attr.Key] = attr.Value;
                        }
                    }

                    result.Nodes.Add(new BinaryNode(
                        "to",
                        new Dictionary<string, string> { { "jid", jid } },
                        new List<BinaryNode> { new BinaryNode("enc", attrs, encrypted.Ciphertext) }));
                }
                catch (Exception ex)
                {
                    // One unreachable device must not cost the other recipients their copy.
                    failures++;
                    _log.Error("[Send] Failed to encrypt for " + jid, ex);
                }
            }

            if (result.Nodes.Count == 0)
            {
                throw new InvalidOperationException(
                    "Could not encrypt for any of the " + recipientJids.Count + " recipient device(s)");
            }

            if (failures > 0)
            {
                _log.Warn("[Send] Skipped " + failures + " device(s) that could not be encrypted for");
            }

            return result;
        }

        /// <summary>
        /// Our own devices - other than the one sending - receive the device-sent form so they
        /// can file the message under the real chat rather than under ourselves.
        /// </summary>
        private static global::Proto.Message SelectPayload(
            string jid,
            global::Proto.Message message,
            global::Proto.Message deviceSentMessage,
            string meId,
            string meLid,
            string ownPnUser,
            string ownLidUser)
        {
            if (deviceSentMessage == null)
            {
                return message;
            }

            var targetUser = JidUtils.GetUser(jid);
            var isOwnUser = targetUser != null &&
                            (targetUser == ownPnUser || (ownLidUser != null && targetUser == ownLidUser));

            var isSendingDevice = jid == meId || (!string.IsNullOrEmpty(meLid) && jid == meLid);

            return isOwnUser && !isSendingDevice ? deviceSentMessage : message;
        }
    }
}
