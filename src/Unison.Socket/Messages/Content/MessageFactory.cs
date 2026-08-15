// =============================================================================
// MessageFactory
//
// Turns what the caller wants to send into the protobuf that says it.
//
// Two things happen here that are easy to miss. Media is uploaded before the
// message exists at all - the message is only a pointer to a file already on
// the CDN, so a failed upload means no message rather than a broken one. And
// context info, which carries mentions, the quote and the disappearing timer,
// is built once and attached while each content type is constructed, because
// the protobuf offers no way to set it on "whatever message this is" after
// the fact.
//
// Edits, deletions, reactions and pins are messages too. They are not commands
// against a message on the server - they are new messages that name an older
// one, which is why they go through the same path and get sent the same way.
//
// Ports: rc14 generateWAMessageContent and generateWAMessageFromContent in
// src/Utils/messages.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Socket.Media;
using Unison.Socket.UseCases.Media;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages.Content
{
    /// <summary>A message ready to relay, with anything the stanza needs stamped on it.</summary>
    public sealed class BuiltMessage
    {
        public global::Proto.Message Content { get; set; }

        /// <summary>
        /// Extra stanza attributes. The edit codes live here: the server routes a deletion or an
        /// edit by this attribute alone, and without it the message arrives as ordinary text.
        /// </summary>
        public IDictionary<string, string> Attributes { get; set; }

        public IList<Unison.Baileys.Protocol.BinaryNode> Nodes { get; set; }
    }

    public sealed class MessageFactory
    {
        /// <summary>Deleting a message of our own.</summary>
        private const string EditDeleteMine = "7";

        /// <summary>Deleting someone else's message as a group admin.</summary>
        private const string EditDeleteOther = "8";

        private const string EditMessage = "1";

        private const string EditPin = "2";

        public MessageFactory(UploadMediaUseCase upload = null)
        {
            Upload = upload;
        }

        /// <summary>
        /// Set by the media module when it attaches. Until then the factory sends text and
        /// refuses attachments, which is the honest failure: there is nowhere to put the file.
        /// </summary>
        public UploadMediaUseCase Upload { get; set; }

        public async Task<BuiltMessage> BuildAsync(
            OutgoingContent content,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            var text = content as TextContent;
            if (text != null)
            {
                return Simple(BuildText(text));
            }

            var media = content as MediaContent;
            if (media != null)
            {
                return Simple(await BuildMediaAsync(media, cancellationToken).ConfigureAwait(false));
            }

            var reaction = content as ReactionContent;
            if (reaction != null)
            {
                return Simple(BuildReaction(reaction));
            }

            var location = content as LocationContent;
            if (location != null)
            {
                return Simple(BuildLocation(location));
            }

            var contacts = content as ContactContent;
            if (contacts != null)
            {
                return Simple(BuildContacts(contacts));
            }

            var delete = content as DeleteContent;
            if (delete != null)
            {
                return BuildDelete(delete);
            }

            var edit = content as EditContent;
            if (edit != null)
            {
                return BuildEdit(edit);
            }

            var pin = content as PinContent;
            if (pin != null)
            {
                return BuildPin(pin);
            }

            throw new NotSupportedException("Cannot send " + content.GetType().Name);
        }

        /// <summary>
        /// Plain text goes out as a conversation when it carries nothing else. The moment there is
        /// a quote, a mention or a timer it becomes an extended text, because a conversation is a
        /// bare string with nowhere to put them.
        /// </summary>
        private static global::Proto.Message BuildText(TextContent content)
        {
            var context = BuildContextInfo(content);
            if (context == null)
            {
                return new global::Proto.Message { Conversation = content.Text ?? string.Empty };
            }

            return new global::Proto.Message
            {
                ExtendedTextMessage = new global::Proto.Message.Types.ExtendedTextMessage
                {
                    Text = content.Text ?? string.Empty,
                    ContextInfo = context
                }
            };
        }

        private async Task<global::Proto.Message> BuildMediaAsync(
            MediaContent content,
            CancellationToken cancellationToken)
        {
            var uploader = Upload;
            if (uploader == null)
            {
                throw new InvalidOperationException("This factory has no uploader, so it cannot send media");
            }

            if (content.Content == null || content.Content.Length == 0)
            {
                throw new ArgumentException("There is nothing to send", nameof(content));
            }

            var upload = await uploader
                .ExecuteAsync(content.Content, content.MediaType, cancellationToken)
                .ConfigureAwait(false);

            var context = BuildContextInfo(content);
            var message = Compose(content, upload, context);

            // A view-once photo is an ordinary photo inside a wrapper the phone knows to burn
            // after reading. The flag on the message itself is set as well: older clients read
            // one and newer ones the other.
            return content.ViewOnce && (content.MediaType == MediaType.Image ||
                                        content.MediaType == MediaType.Video ||
                                        content.MediaType == MediaType.Ptv)
                ? new global::Proto.Message
                {
                    ViewOnceMessageV2 = new global::Proto.Message.Types.FutureProofMessage { Message = message }
                }
                : message;
        }

        private static global::Proto.Message Compose(
            MediaContent content,
            MediaUploadResult upload,
            global::Proto.ContextInfo context)
        {
            switch (content.MediaType)
            {
                case MediaType.Image:
                    return new global::Proto.Message
                    {
                        ImageMessage = new global::Proto.Message.Types.ImageMessage
                        {
                            Url = upload.Url ?? string.Empty,
                            DirectPath = upload.DirectPath ?? string.Empty,
                            MediaKey = ByteString.CopyFrom(upload.MediaKey),
                            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
                            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
                            FileLength = (ulong)upload.FileLength,
                            MediaKeyTimestamp = upload.MediaKeyTimestamp,
                            Mimetype = content.Mimetype ?? "image/jpeg",
                            Caption = content.Caption ?? string.Empty,
                            Width = (uint)content.Width,
                            Height = (uint)content.Height,
                            JpegThumbnail = ToByteString(content.Thumbnail),
                            ViewOnce = content.ViewOnce,
                            ContextInfo = context
                        }
                    };

                case MediaType.Video:
                case MediaType.Gif:
                case MediaType.Ptv:
                    return new global::Proto.Message
                    {
                        VideoMessage = new global::Proto.Message.Types.VideoMessage
                        {
                            Url = upload.Url ?? string.Empty,
                            DirectPath = upload.DirectPath ?? string.Empty,
                            MediaKey = ByteString.CopyFrom(upload.MediaKey),
                            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
                            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
                            FileLength = (ulong)upload.FileLength,
                            MediaKeyTimestamp = upload.MediaKeyTimestamp,
                            Mimetype = content.Mimetype ?? "video/mp4",
                            Caption = content.Caption ?? string.Empty,
                            Seconds = (uint)content.Seconds,
                            Width = (uint)content.Width,
                            Height = (uint)content.Height,
                            GifPlayback = content.MediaType == MediaType.Gif,
                            JpegThumbnail = ToByteString(content.Thumbnail),
                            StreamingSidecar = ToByteString(upload.StreamingSidecar),
                            ViewOnce = content.ViewOnce,
                            ContextInfo = context
                        }
                    };

                case MediaType.Audio:
                case MediaType.Ptt:
                    return new global::Proto.Message
                    {
                        AudioMessage = new global::Proto.Message.Types.AudioMessage
                        {
                            Url = upload.Url ?? string.Empty,
                            DirectPath = upload.DirectPath ?? string.Empty,
                            MediaKey = ByteString.CopyFrom(upload.MediaKey),
                            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
                            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
                            FileLength = (ulong)upload.FileLength,
                            MediaKeyTimestamp = upload.MediaKeyTimestamp,
                            Mimetype = content.Mimetype ?? "audio/ogg; codecs=opus",
                            Seconds = (uint)content.Seconds,
                            Ptt = content.MediaType == MediaType.Ptt,
                            Waveform = ToByteString(content.Waveform),
                            StreamingSidecar = ToByteString(upload.StreamingSidecar),
                            ContextInfo = context
                        }
                    };

                case MediaType.Sticker:
                    return new global::Proto.Message
                    {
                        StickerMessage = new global::Proto.Message.Types.StickerMessage
                        {
                            Url = upload.Url ?? string.Empty,
                            DirectPath = upload.DirectPath ?? string.Empty,
                            MediaKey = ByteString.CopyFrom(upload.MediaKey),
                            FileSha256 = ByteString.CopyFrom(upload.FileSha256),
                            FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
                            FileLength = (ulong)upload.FileLength,
                            MediaKeyTimestamp = upload.MediaKeyTimestamp,
                            Mimetype = content.Mimetype ?? "image/webp",
                            Width = (uint)content.Width,
                            Height = (uint)content.Height,
                            IsAnimated = content.IsAnimatedSticker,
                            ContextInfo = context
                        }
                    };

                case MediaType.Document:
                    var document = new global::Proto.Message.Types.DocumentMessage
                    {
                        Url = upload.Url ?? string.Empty,
                        DirectPath = upload.DirectPath ?? string.Empty,
                        MediaKey = ByteString.CopyFrom(upload.MediaKey),
                        FileSha256 = ByteString.CopyFrom(upload.FileSha256),
                        FileEncSha256 = ByteString.CopyFrom(upload.FileEncSha256),
                        FileLength = (ulong)upload.FileLength,
                        MediaKeyTimestamp = upload.MediaKeyTimestamp,
                        Mimetype = content.Mimetype ?? "application/octet-stream",
                        FileName = content.FileName ?? string.Empty,
                        Title = content.FileName ?? string.Empty,
                        PageCount = (uint)content.PageCount,
                        JpegThumbnail = ToByteString(content.Thumbnail),
                        ContextInfo = context
                    };

                    if (string.IsNullOrEmpty(content.Caption))
                    {
                        return new global::Proto.Message { DocumentMessage = document };
                    }

                    // A document with a caption is a different message type, not a field: the
                    // phone shows the caption as a separate bubble under the file.
                    document.Caption = content.Caption;
                    return new global::Proto.Message
                    {
                        DocumentWithCaptionMessage = new global::Proto.Message.Types.FutureProofMessage
                        {
                            Message = new global::Proto.Message { DocumentMessage = document }
                        }
                    };

                default:
                    throw new NotSupportedException("Cannot send media of type " + content.MediaType);
            }
        }

        private static global::Proto.Message BuildReaction(ReactionContent content)
        {
            return new global::Proto.Message
            {
                ReactionMessage = new global::Proto.Message.Types.ReactionMessage
                {
                    Key = ToKey(content.Key),

                    // An empty text is how a reaction is taken back; there is no separate removal.
                    Text = content.Emoji ?? string.Empty,
                    SenderTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }
            };
        }

        private static global::Proto.Message BuildLocation(LocationContent content)
        {
            return new global::Proto.Message
            {
                LocationMessage = new global::Proto.Message.Types.LocationMessage
                {
                    DegreesLatitude = content.Latitude,
                    DegreesLongitude = content.Longitude,
                    Name = content.Name ?? string.Empty,
                    Address = content.Address ?? string.Empty,
                    ContextInfo = BuildContextInfo(content)
                }
            };
        }

        private static global::Proto.Message BuildContacts(ContactContent content)
        {
            var context = BuildContextInfo(content);

            if (content.Vcards.Count == 1)
            {
                return new global::Proto.Message
                {
                    ContactMessage = new global::Proto.Message.Types.ContactMessage
                    {
                        DisplayName = content.DisplayName ?? string.Empty,
                        Vcard = content.Vcards[0],
                        ContextInfo = context
                    }
                };
            }

            var array = new global::Proto.Message.Types.ContactsArrayMessage
            {
                DisplayName = content.DisplayName ?? string.Empty,
                ContextInfo = context
            };

            foreach (var vcard in content.Vcards)
            {
                array.Contacts.Add(new global::Proto.Message.Types.ContactMessage { Vcard = vcard });
            }

            return new global::Proto.Message { ContactsArrayMessage = array };
        }

        /// <summary>
        /// A deletion is a protocol message naming the victim. The edit attribute tells the server
        /// whether we are deleting our own message or moderating someone else's, and it refuses
        /// the second unless we really are an admin of that group.
        /// </summary>
        private static BuiltMessage BuildDelete(DeleteContent content)
        {
            var key = content.Key;
            var isModeration = !key.FromMe && JidUtils.GetServer(key.RemoteJid) == JidUtils.ServerGroup;

            return new BuiltMessage
            {
                Content = new global::Proto.Message
                {
                    ProtocolMessage = new global::Proto.Message.Types.ProtocolMessage
                    {
                        Key = ToKey(key),
                        Type = global::Proto.Message.Types.ProtocolMessage.Types.Type.Revoke
                    }
                },
                Attributes = new Dictionary<string, string>
                {
                    { "edit", isModeration ? EditDeleteOther : EditDeleteMine }
                }
            };
        }

        private static BuiltMessage BuildEdit(EditContent content)
        {
            var edited = new global::Proto.Message { Conversation = content.Text ?? string.Empty };

            return new BuiltMessage
            {
                Content = new global::Proto.Message
                {
                    EditedMessage = new global::Proto.Message.Types.FutureProofMessage
                    {
                        Message = new global::Proto.Message
                        {
                            ProtocolMessage = new global::Proto.Message.Types.ProtocolMessage
                            {
                                Key = ToKey(content.Key),
                                Type = global::Proto.Message.Types.ProtocolMessage.Types.Type.MessageEdit,
                                EditedMessage = edited,
                                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                            }
                        }
                    }
                },
                Attributes = new Dictionary<string, string> { { "edit", EditMessage } }
            };
        }

        private static BuiltMessage BuildPin(PinContent content)
        {
            return new BuiltMessage
            {
                Content = new global::Proto.Message
                {
                    PinInChatMessage = new global::Proto.Message.Types.PinInChatMessage
                    {
                        Key = ToKey(content.Key),
                        Type = content.Pin
                            ? global::Proto.Message.Types.PinInChatMessage.Types.Type.PinForAll
                            : global::Proto.Message.Types.PinInChatMessage.Types.Type.UnpinForAll,
                        SenderTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    },
                    MessageContextInfo = new global::Proto.MessageContextInfo
                    {
                        // How long the pin lasts is carried here rather than on the pin itself.
                        MessageAddOnDurationInSecs = content.Pin ? (uint)content.Seconds : 0
                    }
                },
                Attributes = new Dictionary<string, string> { { "edit", EditPin } }
            };
        }

        /// <summary>
        /// Builds the context info shared by every content type, or null when there is nothing to
        /// say. Null matters: an empty context info is still a field on the wire, and some clients
        /// treat its presence as a quote with a missing body.
        /// </summary>
        private static global::Proto.ContextInfo BuildContextInfo(OutgoingContent content)
        {
            var hasMentions = content.Mentions != null && content.Mentions.Count > 0;
            var hasQuote = content.Quoted != null && content.Quoted.Key != null;

            if (!hasMentions && !hasQuote && content.EphemeralExpiration <= 0)
            {
                return null;
            }

            var context = new global::Proto.ContextInfo();

            if (hasMentions)
            {
                foreach (var mention in content.Mentions)
                {
                    context.MentionedJid.Add(mention);
                }
            }

            if (content.EphemeralExpiration > 0)
            {
                context.Expiration = (uint)content.EphemeralExpiration;
            }

            if (hasQuote)
            {
                var quoted = content.Quoted;
                var participant = quoted.Participant ??
                                  quoted.Key.Participant ??
                                  quoted.Key.RemoteJid;

                context.StanzaId = quoted.Key.Id;
                context.Participant = JidUtils.NormalizedUser(participant);
                context.QuotedMessage = quoted.Message;
            }

            return context;
        }

        private static BuiltMessage Simple(global::Proto.Message message)
        {
            return new BuiltMessage { Content = message };
        }

        private static global::Proto.MessageKey ToKey(MessageEnvelopeKey key)
        {
            if (key == null)
            {
                throw new ArgumentException("A message key is required");
            }

            var result = new global::Proto.MessageKey
            {
                Id = key.Id ?? string.Empty,
                RemoteJid = key.RemoteJid ?? string.Empty,
                FromMe = key.FromMe
            };

            if (!string.IsNullOrEmpty(key.Participant))
            {
                result.Participant = key.Participant;
            }

            return result;
        }

        private static ByteString ToByteString(byte[] value)
        {
            return value != null && value.Length > 0 ? ByteString.CopyFrom(value) : ByteString.Empty;
        }
    }
}
