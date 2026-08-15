// =============================================================================
// RelayMessageUseCase
//
// Turns a message into the stanza that carries it, whoever it is going to.
//
// There are four shapes hiding in one function, and they are kept together
// because they share the tail end - the participants wrapper, the device
// identity, the tctoken, the retry cache:
//
//   newsletter  a single plaintext node, no encryption at all
//   group       one skmsg for everyone, plus a sender key for devices that lack it
//   1:1         one encrypted copy per device, ours getting the device-sent form
//   retry       one copy for one device, answering a retry receipt
//
// Ports: rc14 relayMessage in src/Socket/messages-send.ts
//
// Left out on purpose, because the host still owns them: the reporting token,
// tctoken issuance after send, and the newsletter-specific encoding. The tctoken
// itself is attached through a delegate so the existing store keeps serving it.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Models;
using Unison.Socket.Session;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    /// <summary>The single device a retry resend is addressed to, and the peer's attempt count.</summary>
    public sealed class RelayParticipant
    {
        public string Jid { get; set; }

        public int Count { get; set; }
    }

    public sealed class RelayOptions
    {
        /// <summary>Leave null to mint one.</summary>
        public string MessageId { get; set; }

        /// <summary>Set only when answering a retry receipt.</summary>
        public RelayParticipant Participant { get; set; }

        /// <summary>Extra stanza attributes: category, edit, push_priority and so on.</summary>
        public IDictionary<string, string> AdditionalAttributes { get; set; }

        /// <summary>Extra children appended after the encrypted content.</summary>
        public IList<BinaryNode> AdditionalNodes { get; set; }

        public bool UseUserDevicesCache { get; set; }

        /// <summary>Recipients of a status update, which has no participant list of its own.</summary>
        public IList<string> StatusJidList { get; set; }

        public RelayOptions()
        {
            UseUserDevicesCache = true;
        }
    }

    public sealed class RelayMessageUseCase
    {
        private const string StatusJid = "status@broadcast";

        /// <summary>Prefix of the entry that stamps a sender key memory list with its key id.</summary>
        private const string KeyIdMarker = "$keyid:";

        private readonly ConnectionHandler _connection;
        private readonly AuthState _authState;
        private readonly ISignalRepository _signal;
        private readonly GetUSyncDevicesUseCase _devices;
        private readonly AssertSessionsUseCase _sessions;
        private readonly CreateParticipantNodesUseCase _participants;
        private readonly ISocketLog _log;

        public RelayMessageUseCase(
            ConnectionHandler connection,
            AuthState authState,
            ISignalRepository signal,
            GetUSyncDevicesUseCase devices,
            AssertSessionsUseCase sessions,
            CreateParticipantNodesUseCase participants,
            ISocketLog log = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            if (authState == null)
            {
                throw new ArgumentNullException(nameof(authState));
            }

            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            _connection = connection;
            _authState = authState;
            _signal = signal;
            _devices = devices;
            _sessions = sessions;
            _participants = participants;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>Resolves a group's metadata, from cache where the host has one.</summary>
        public Func<string, Task<GroupMetadata>> GetGroupMetadata { get; set; }

        /// <summary>Supplies the trusted contact token for a 1:1 chat, when one is stored.</summary>
        public Func<string, Task<byte[]>> GetTrustedContactToken { get; set; }

        /// <summary>Records outgoing messages so a retry receipt can be answered later.</summary>
        public MessageRetryManager RetryManager { get; set; }

        /// <summary>
        /// Announces that the record of who holds a group's sender key has changed, so the host
        /// writes it down - rc14 does the same with keys.set('sender-key-memory'). Without it the
        /// record dies with the process and every cold start hands the key to every device of
        /// every member again.
        /// </summary>
        public Func<Task> SenderKeyMemoryChanged { get; set; }

        /// <returns>The id the message was sent with.</returns>
        public async Task<string> ExecuteAsync(string jid, global::Proto.Message message, RelayOptions options = null)
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentNullException(nameof(jid));
            }

            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            options = options ?? new RelayOptions();

            var meId = _authState.Me != null ? _authState.Me.Id : null;
            if (string.IsNullOrEmpty(meId))
            {
                throw new InvalidOperationException("Cannot relay a message before login");
            }

            var meLid = _authState.Me != null ? _authState.Me.Lid : null;
            var messageId = string.IsNullOrEmpty(options.MessageId)
                ? MessageContent.GenerateMessageId(meId)
                : options.MessageId;

            var isRetryResend = options.Participant != null && !string.IsNullOrEmpty(options.Participant.Jid);
            var server = JidUtils.GetServer(jid);
            var isGroup = server == JidUtils.ServerGroup;
            var isStatus = jid == StatusJid;
            var isNewsletter = server == JidUtils.ServerNewsletter;
            var isLid = server == JidUtils.ServerLid;
            var destinationJid = isStatus ? StatusJid : jid;

            var attributes = options.AdditionalAttributes == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(options.AdditionalAttributes);

            if (isNewsletter)
            {
                return await SendNewsletterAsync(jid, message, messageId, attributes).ConfigureAwait(false);
            }

            // Every ordinary message carries the device list version the recipient checks to see
            // whether we knew about all of their devices. A peer message is deliberately left
            // alone: our own primary accepts the stanza but ignores the request inside it when we
            // add this.
            string relayCategory;
            attributes.TryGetValue("category", out relayCategory);
            if (relayCategory != "peer" && message.MessageContextInfo == null)
            {
                message.MessageContextInfo = new global::Proto.MessageContextInfo
                {
                    DeviceListMetadata = new global::Proto.DeviceListMetadata(),
                    DeviceListMetadataVersion = 2
                };
            }

            var content = new List<BinaryNode>();
            var participantNodes = new List<BinaryNode>();
            var shouldIncludeDeviceIdentity = isRetryResend;
            GroupContent groupResult = null;

            var extraAttrs = new Dictionary<string, string>();
            var mediaType = MessageContent.GetMediaType(MessageContent.Normalize(message));
            if (!string.IsNullOrEmpty(mediaType))
            {
                extraAttrs["mediatype"] = mediaType;
            }

            if (MessageContent.ShouldHideDecryptFailure(message))
            {
                extraAttrs["decrypt-fail"] = "hide";
            }

            // The wrapped form our own other devices receive, so they can file the message
            // under the chat it belongs to instead of under ourselves.
            var deviceSentMessage = new global::Proto.Message
            {
                DeviceSentMessage = new global::Proto.Message.Types.DeviceSentMessage
                {
                    DestinationJid = destinationJid,
                    Message = message
                },
                MessageContextInfo = message.MessageContextInfo
            };

            var devices = new List<DeviceJid>();
            if (options.Participant != null && !string.IsNullOrEmpty(options.Participant.Jid))
            {
                if (!isGroup && !isStatus)
                {
                    // Without this the server fans the resend out to every device, and the copy
                    // encrypted for one of them fails to decrypt everywhere else.
                    attributes["device_fanout"] = "false";
                }

                devices.Add(new DeviceJid
                {
                    User = JidUtils.GetUser(options.Participant.Jid),
                    Device = JidUtils.GetDevice(options.Participant.Jid),
                    Jid = options.Participant.Jid
                });
            }

            if ((isGroup || isStatus) && !isRetryResend)
            {
                groupResult = await BuildGroupContentAsync(
                    jid,
                    destinationJid,
                    message,
                    meId,
                    meLid,
                    isGroup,
                    isStatus,
                    options,
                    attributes,
                    extraAttrs).ConfigureAwait(false);

                content.Add(groupResult.EncNode);
                participantNodes.AddRange(groupResult.ParticipantNodes);
                shouldIncludeDeviceIdentity = shouldIncludeDeviceIdentity || groupResult.ShouldIncludeDeviceIdentity;
            }
            else if (!isRetryResend)
            {
                var directResult = await BuildDirectContentAsync(
                    jid,
                    message,
                    deviceSentMessage,
                    meId,
                    meLid,
                    isLid,
                    attributes,
                    extraAttrs,
                    options).ConfigureAwait(false);

                participantNodes.AddRange(directResult.ParticipantNodes);
                shouldIncludeDeviceIdentity = shouldIncludeDeviceIdentity || directResult.ShouldIncludeDeviceIdentity;
            }

            if (isRetryResend)
            {
                content.Add(await BuildRetryEncNodeAsync(
                    message,
                    destinationJid,
                    options.Participant,
                    meId,
                    meLid,
                    isGroup || isStatus).ConfigureAwait(false));
            }

            if (participantNodes.Count > 0)
            {
                string category;
                attributes.TryGetValue("category", out category);

                if (category == "peer")
                {
                    // A peer message goes to exactly one device, so its enc node sits directly
                    // in the stanza rather than inside a participants wrapper.
                    var peerNode = participantNodes[0].GetChildren("enc").FirstOrDefault();
                    if (peerNode != null)
                    {
                        content.Add(peerNode);
                    }
                }
                else
                {
                    content.Add(new BinaryNode("participants", null, participantNodes));
                }
            }

            var stanzaAttrs = new Dictionary<string, string>
            {
                { "id", messageId },
                { "type", MessageContent.GetMessageType(message) }
            };

            foreach (var attr in attributes)
            {
                stanzaAttrs[attr.Key] = attr.Value;
            }

            ApplyDestination(stanzaAttrs, destinationJid, options.Participant, meId);

            if (shouldIncludeDeviceIdentity)
            {
                // The first message of a session carries the proof that this companion belongs
                // to the account. Sending the stanza without it is worse than failing: the
                // server takes it and the recipient is left with something it will never open.
                var identity = KeyBundleNodes.EncodeSignedDeviceIdentity(_authState.Account, true);
                if (identity == null)
                {
                    throw new InvalidOperationException(
                        "Cannot open a session without the signed device identity; the account was not restored from storage");
                }

                content.Add(new BinaryNode("device-identity", null, identity));
            }

            var isPeerMessage = stanzaAttrs.ContainsKey("category") && stanzaAttrs["category"] == "peer";
            if (!isGroup && !isStatus && !isRetryResend && !isPeerMessage && GetTrustedContactToken != null)
            {
                var token = await GetTrustedContactToken(destinationJid).ConfigureAwait(false);
                if (token != null && token.Length > 0)
                {
                    content.Add(new BinaryNode("tctoken", null, token));
                }
            }

            if (options.AdditionalNodes != null)
            {
                foreach (var node in options.AdditionalNodes.Where(n => n != null))
                {
                    content.Add(node);
                }
            }

            await _connection.SendNodeAsync(new BinaryNode("message", stanzaAttrs, content)).ConfigureAwait(false);
            _log.Debug("[Send] Relayed " + messageId + " to " + destinationJid + " across " + participantNodes.Count + " device(s)");

            if (groupResult != null && groupResult.SenderKeyMemory != null)
            {
                groupResult.SenderKeyMemory.AddRange(groupResult.NewlyKeyedDevices);

                if (SenderKeyMemoryChanged != null)
                {
                    await SenderKeyMemoryChanged().ConfigureAwait(false);
                }
            }

            if (RetryManager != null && options.Participant == null)
            {
                RetryManager.AddRecentMessage(destinationJid, messageId, message);
            }

            return messageId;
        }

        /// <summary>Newsletters are public, so the message travels in the clear.</summary>
        private async Task<string> SendNewsletterAsync(
            string jid,
            global::Proto.Message message,
            string messageId,
            IDictionary<string, string> attributes)
        {
            var attrs = new Dictionary<string, string>
            {
                { "to", jid },
                { "id", messageId },
                { "type", MessageContent.GetMessageType(message) }
            };

            foreach (var attr in attributes)
            {
                attrs[attr.Key] = attr.Value;
            }

            var content = new List<BinaryNode> { new BinaryNode("plaintext", null, message.ToByteArray()) };

            await _connection.SendNodeAsync(new BinaryNode("message", attrs, content)).ConfigureAwait(false);
            _log.Debug("[Send] Relayed newsletter message " + messageId + " to " + jid);
            return messageId;
        }

        /// <summary>
        /// Encrypts once to the group's sender key, and hands that key to any device that has
        /// not received it yet. The memory of who already has it is what keeps a group message
        /// from being a full fan-out every time.
        /// </summary>
        private async Task<GroupContent> BuildGroupContentAsync(
            string jid,
            string destinationJid,
            global::Proto.Message message,
            string meId,
            string meLid,
            bool isGroup,
            bool isStatus,
            RelayOptions options,
            IDictionary<string, string> attributes,
            IDictionary<string, string> extraAttrs)
        {
            var result = new GroupContent();

            var participantJids = new List<string>();
            GroupMetadata metadata = null;

            if (!isStatus && GetGroupMetadata != null)
            {
                metadata = await GetGroupMetadata(jid).ConfigureAwait(false);
                if (metadata != null && metadata.Participants != null)
                {
                    participantJids.AddRange(metadata.Participants.Select(p => p.Id).Where(id => !string.IsNullOrEmpty(id)));
                }
            }

            // A group message is encrypted once, to a sender key. Whoever is missing from this
            // list never receives that key, so sending without it produces a message that looks
            // delivered and cannot be read - worse than refusing to send.
            if (isGroup && participantJids.Count == 0)
            {
                throw new InvalidOperationException(
                    "Cannot send to " + jid + " without its participant list");
            }

            if (metadata != null && metadata.EphemeralDuration > 0)
            {
                attributes["expiration"] = metadata.EphemeralDuration.ToString();
            }

            if (isStatus && options.StatusJidList != null)
            {
                participantJids.AddRange(options.StatusJidList);
            }

            if (_devices != null && participantJids.Count > 0)
            {
                var enumerated = await _devices
                    .ExecuteAsync(participantJids, options.UseUserDevicesCache, false)
                    .ConfigureAwait(false);

                result.Devices.AddRange(enumerated);
            }

            // The group decides which of our identities signs its messages, so a guess here does
            // not degrade gracefully: the recipients derive the sender key from an address we
            // never used, and the message arrives as one they will never open.
            string addressingModeSource;
            var addressingMode = ResolveAddressingMode(metadata, meLid, out addressingModeSource);

            if (isGroup)
            {
                attributes["addressing_mode"] = addressingMode;
            }

            var senderIdentity = addressingMode == "lid" && !string.IsNullOrEmpty(meLid) ? meLid : meId;

            var encrypted = await _signal
                .EncryptGroupMessageAsync(destinationJid, senderIdentity, message.ToByteArray())
                .ConfigureAwait(false);

            var senderKeyMemory = GetSenderKeyMemory(
                jid,
                senderIdentity,
                encrypted.KeyId,
                encrypted.CreatedNewSenderKey);

            var alreadyHasKey = new HashSet<string>(
                senderKeyMemory.Where(entry => !entry.StartsWith(KeyIdMarker, StringComparison.Ordinal)),
                StringComparer.Ordinal);

            var needSenderKey = new List<string>();

            foreach (var device in result.Devices)
            {
                var deviceJid = device.Jid;
                if (string.IsNullOrEmpty(deviceJid) ||
                    alreadyHasKey.Contains(deviceJid) ||
                    JidUtils.IsHostedLidUser(deviceJid) ||
                    JidUtils.IsHostedPnUser(deviceJid) ||
                    device.Device == JidUtils.HostedDeviceId)
                {
                    continue;
                }

                needSenderKey.Add(deviceJid);
            }

            if (needSenderKey.Count > 0 && _participants != null)
            {
                var senderKeyMessage = new global::Proto.Message
                {
                    SenderKeyDistributionMessage = new global::Proto.Message.Types.SenderKeyDistributionMessage
                    {
                        GroupId = destinationJid,
                        AxolotlSenderKeyDistributionMessage =
                            ByteString.CopyFrom(encrypted.SenderKeyDistributionMessage)
                    }
                };

                if (_sessions != null)
                {
                    await _sessions.ExecuteAsync(needSenderKey).ConfigureAwait(false);
                }

                var nodes = await _participants
                    .ExecuteAsync(needSenderKey, senderKeyMessage, extraAttrs)
                    .ConfigureAwait(false);

                result.ParticipantNodes.AddRange(nodes.Nodes);
                result.ShouldIncludeDeviceIdentity = nodes.ShouldIncludeDeviceIdentity;

                // Written down only once the stanza is away. Recording it here instead would
                // mean a send that throws still leaves those devices marked as holding the key,
                // and the next message skips them - for good.
                result.SenderKeyMemory = senderKeyMemory;
                result.NewlyKeyedDevices.AddRange(needSenderKey.Where(alreadyHasKey.Add));

                _log.Debug("[Send] Distributed the sender key of " + jid + " to " + needSenderKey.Count + " device(s)");
            }

            var encAttrs = new Dictionary<string, string> { { "v", "2" }, { "type", "skmsg" } };
            foreach (var attr in extraAttrs)
            {
                encAttrs[attr.Key] = attr.Value;
            }

            result.EncNode = new BinaryNode("enc", encAttrs, encrypted.Ciphertext);

            _log.Debug(
                "[Send] Group " + jid + ": addressing=" + addressingMode + " (" + addressingModeSource +
                "), signer=" + senderIdentity +
                ", keyId=" + encrypted.KeyId +
                ", participants=" + participantJids.Count +
                ", devices=" + result.Devices.Count +
                ", keyGoesTo=" + needSenderKey.Count);

            return result;
        }

        /// <summary>
        /// Which address space a group's messages travel in. What the server said, and lid when
        /// it said nothing - rc14 relayMessage, messages-send.ts.
        ///
        /// The one addition is the guard on having a LID at all. rc14 defaults to lid and then
        /// signs with the phone number when there is no lid to sign with, which would stamp the
        /// stanza with one identity and the sender key with the other, leaving every member
        /// holding a message whose key they cannot find. It never happens there because a
        /// companion always has a LID once paired; here it can, while credentials are still
        /// being restored, so the fallback follows what we can actually sign under.
        /// </summary>
        private static string ResolveAddressingMode(GroupMetadata metadata, string meLid, out string source)
        {
            if (metadata != null && metadata.AddressingMode == GroupAddressingMode.Lid)
            {
                source = "server";
                return "lid";
            }

            if (metadata != null && metadata.AddressingMode == GroupAddressingMode.Pn)
            {
                source = "server";
                return "pn";
            }

            if (string.IsNullOrEmpty(meLid))
            {
                source = "no-lid-of-our-own";
                return "pn";
            }

            source = "default";
            return "lid";
        }

        /// <summary>
        /// Enumerates both sides' devices and encrypts a copy for each, keeping our own identity
        /// in the same address space as the conversation.
        /// </summary>
        private async Task<DirectContent> BuildDirectContentAsync(
            string jid,
            global::Proto.Message message,
            global::Proto.Message deviceSentMessage,
            string meId,
            string meLid,
            bool isLid,
            IDictionary<string, string> attributes,
            IDictionary<string, string> extraAttrs,
            RelayOptions options)
        {
            var result = new DirectContent();

            var ownId = isLid && !string.IsNullOrEmpty(meLid) ? meLid : meId;
            var ownUser = JidUtils.GetUser(ownId);
            var targetUser = JidUtils.GetUser(jid);
            var userServer = isLid ? JidUtils.ServerLid : JidUtils.ServerWhatsApp;

            var devices = new List<DeviceJid>();

            string category;
            attributes.TryGetValue("category", out category);

            if (category == "peer")
            {
                // Peer messages address the account, not a device list.
                devices.Add(new DeviceJid { User = targetUser, Device = 0, Jid = WA.JidEncode(targetUser, userServer, 0) });

                if (targetUser != ownUser)
                {
                    devices.Add(new DeviceJid { User = ownUser, Device = 0, Jid = WA.JidEncode(ownUser, userServer, 0) });
                }
            }
            else if (_devices != null)
            {
                var senderIdentity = WA.JidEncode(ownUser, userServer, 0);
                var enumerated = await _devices
                    .ExecuteAsync(new[] { senderIdentity, jid }, options.UseUserDevicesCache, false)
                    .ConfigureAwait(false);

                devices.AddRange(enumerated);
            }

            var mePnUser = JidUtils.GetUser(meId);
            var meLidUser = JidUtils.GetUser(meLid);

            var mine = new List<string>();
            var theirs = new List<string>();

            foreach (var device in devices)
            {
                var deviceJid = device.Jid;
                if (string.IsNullOrEmpty(deviceJid))
                {
                    continue;
                }

                // We already have the message we are sending.
                if (deviceJid == meId || (!string.IsNullOrEmpty(meLid) && deviceJid == meLid))
                {
                    continue;
                }

                if (device.User == mePnUser || (meLidUser != null && device.User == meLidUser))
                {
                    mine.Add(deviceJid);
                }
                else
                {
                    theirs.Add(deviceJid);
                }
            }

            if (mine.Count == 0 && theirs.Count == 0)
            {
                return result;
            }

            if (_sessions != null)
            {
                await _sessions.ExecuteAsync(mine.Concat(theirs)).ConfigureAwait(false);
            }

            if (_participants == null)
            {
                return result;
            }

            if (mine.Count > 0)
            {
                var ours = await _participants.ExecuteAsync(mine, deviceSentMessage, extraAttrs).ConfigureAwait(false);
                result.ParticipantNodes.AddRange(ours.Nodes);
                result.ShouldIncludeDeviceIdentity |= ours.ShouldIncludeDeviceIdentity;
            }

            if (theirs.Count > 0)
            {
                var others = await _participants
                    .ExecuteAsync(theirs, message, extraAttrs, deviceSentMessage)
                    .ConfigureAwait(false);

                result.ParticipantNodes.AddRange(others.Nodes);
                result.ShouldIncludeDeviceIdentity |= others.ShouldIncludeDeviceIdentity;
            }

            extraAttrs["phash"] = MessageContent.GenerateParticipantHashV2(mine.Concat(theirs));
            return result;
        }

        /// <summary>
        /// Re-encrypts a message for the one device that could not read it. In a group the
        /// sender key rides along, because the usual reason the peer failed is that it never
        /// received one.
        /// </summary>
        private async Task<BinaryNode> BuildRetryEncNodeAsync(
            global::Proto.Message message,
            string destinationJid,
            RelayParticipant participant,
            string meId,
            string meLid,
            bool isGroupOrStatus)
        {
            var payload = message;

            if (isGroupOrStatus)
            {
                var senderIdentity = await ResolveGroupSenderIdentityAsync(destinationJid, meId, meLid).ConfigureAwait(false);
                if (senderIdentity != null)
                {
                    try
                    {
                        var skdm = await _signal
                            .GetSenderKeyDistributionMessageAsync(destinationJid, senderIdentity)
                            .ConfigureAwait(false);

                        if (skdm != null)
                        {
                            payload = message.Clone();
                            payload.SenderKeyDistributionMessage = new global::Proto.Message.Types.SenderKeyDistributionMessage
                            {
                                GroupId = destinationJid,
                                AxolotlSenderKeyDistributionMessage = ByteString.CopyFrom(skdm)
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("[Send] Could not attach a sender key to the retry for " + destinationJid, ex);
                    }
                }
            }

            var isMe = JidUtils.AreSameUser(
                participant.Jid,
                JidUtils.IsAnyLid(participant.Jid) ? meLid : meId);

            if (isMe)
            {
                payload = new global::Proto.Message
                {
                    DeviceSentMessage = new global::Proto.Message.Types.DeviceSentMessage
                    {
                        DestinationJid = destinationJid,
                        Message = payload
                    }
                };
            }

            var encrypted = await _signal
                .EncryptMessageAsync(participant.Jid, payload.ToByteArray())
                .ConfigureAwait(false);

            return new BinaryNode(
                "enc",
                new Dictionary<string, string>
                {
                    { "v", "2" },
                    { "type", encrypted.Type },
                    { "count", participant.Count.ToString() }
                },
                encrypted.Ciphertext);
        }

        /// <summary>Which of our identities the group's sender key was created under.</summary>
        private async Task<string> ResolveGroupSenderIdentityAsync(string groupJid, string meId, string meLid)
        {
            if (!string.IsNullOrEmpty(meLid) &&
                await _signal.HasSenderKeyAsync(groupJid, meLid).ConfigureAwait(false))
            {
                return meLid;
            }

            return await _signal.HasSenderKeyAsync(groupJid, meId).ConfigureAwait(false) ? meId : null;
        }

        /// <summary>
        /// A retry goes to one device only. In a group that means naming the participant while
        /// still addressing the group; in a 1:1 with one of our own devices the destination and
        /// the recipient swap places.
        /// </summary>
        private static void ApplyDestination(
            IDictionary<string, string> attrs,
            string destinationJid,
            RelayParticipant participant,
            string meId)
        {
            if (participant == null || string.IsNullOrEmpty(participant.Jid))
            {
                attrs["to"] = destinationJid;
                return;
            }

            if (JidUtils.IsGroup(destinationJid))
            {
                attrs["to"] = destinationJid;
                attrs["participant"] = participant.Jid;
                return;
            }

            if (JidUtils.AreSameUser(participant.Jid, meId))
            {
                attrs["to"] = participant.Jid;
                attrs["recipient"] = destinationJid;
                return;
            }

            attrs["to"] = participant.Jid;
        }

        /// <summary>
        /// The devices already holding our sender key for a group. The list is the one the host
        /// persists, so adding to it is what makes the next message a cheap one.
        /// </summary>
        /// <summary>
        /// Forgets who holds the sender key of a group, so the next message to it distributes the
        /// key again. Answering a retry means someone in the group could not read us, and the
        /// memory is the only thing standing between them and a key they can use.
        /// </summary>
        public void ForgetSenderKeyMemory(string groupJid)
        {
            if (string.IsNullOrEmpty(groupJid))
            {
                return;
            }

            var prefix = groupJid + "|";
            var stale = _authState.SenderKeyMemory.Keys
                .Where(key => key == groupJid || key.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            foreach (var key in stale)
            {
                _authState.SenderKeyMemory.Remove(key);
            }

            if (stale.Count > 0)
            {
                _log.Debug("[Send] Forgot who holds the sender key of " + groupJid);
            }
        }

        /// <summary>
        /// The devices already holding the sender key we are about to use. The memory belongs to a
        /// key, not to a group: after a rotation - or when the group switches us to our other
        /// identity, which mints a key of its own - nobody holds the new key yet, and skipping the
        /// distribution leaves every member with a message they cannot open.
        /// The first entry marks which key the list describes.
        /// </summary>
        private List<string> GetSenderKeyMemory(
            string groupJid,
            string senderIdentity,
            int keyId,
            bool createdNewSenderKey)
        {
            var memoryKey = groupJid + "|" + senderIdentity;
            var marker = KeyIdMarker + keyId;

            List<string> known;
            var found = _authState.SenderKeyMemory.TryGetValue(memoryKey, out known);
            var stale = createdNewSenderKey || !found || known == null || !known.Contains(marker);

            if (stale)
            {
                known = new List<string> { marker };
                _authState.SenderKeyMemory[memoryKey] = known;
                _log.Debug("[Send] Sender key " + keyId + " of " + groupJid + " is new to every member");
            }

            return known;
        }

        private sealed class GroupContent
        {
            public GroupContent()
            {
                Devices = new List<DeviceJid>();
                ParticipantNodes = new List<BinaryNode>();
                NewlyKeyedDevices = new List<string>();
            }

            public List<DeviceJid> Devices { get; private set; }

            public List<BinaryNode> ParticipantNodes { get; private set; }

            public bool ShouldIncludeDeviceIdentity { get; set; }

            public BinaryNode EncNode { get; set; }

            /// <summary>The list to append to once the stanza has actually left. Null when nothing changed.</summary>
            public List<string> SenderKeyMemory { get; set; }

            public List<string> NewlyKeyedDevices { get; private set; }
        }

        private sealed class DirectContent
        {
            public DirectContent()
            {
                ParticipantNodes = new List<BinaryNode>();
            }

            public List<BinaryNode> ParticipantNodes { get; private set; }

            public bool ShouldIncludeDeviceIdentity { get; set; }
        }
    }
}
