// =============================================================================
// OutgoingContent
//
// What the caller wants to send, before it becomes protobuf.
//
// Baileys takes a single object and works out which of forty shapes it is by
// looking for keys on it. That reads well in TypeScript and badly anywhere
// else, so the shapes are types here: a caller says what it is sending and the
// compiler holds it to the fields that shape actually has.
//
// The pieces every message can carry - mentions, a quote, view-once, the
// chat's disappearing timer - live on the base, because on the wire they all
// end up in the same context info regardless of what is being sent.
//
// Ports: rc14 AnyMessageContent and its neighbours in src/Types/Message.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Socket.Messages;

namespace Unison.Socket.Messages.Content
{
    /// <summary>The message being replied to, as far as the quote needs to know.</summary>
    public sealed class QuotedMessage
    {
        public MessageEnvelopeKey Key { get; set; }

        public global::Proto.Message Message { get; set; }

        /// <summary>Who wrote it, when the key does not say.</summary>
        public string Participant { get; set; }
    }

    public abstract class OutgoingContent
    {
        protected OutgoingContent()
        {
            Mentions = new List<string>();
        }

        /// <summary>JIDs written as @mentions in the text; the phone highlights these.</summary>
        public IList<string> Mentions { get; private set; }

        public QuotedMessage Quoted { get; set; }

        /// <summary>Seconds until the message disappears, or zero when the chat has no timer.</summary>
        public int EphemeralExpiration { get; set; }
    }

    public sealed class TextContent : OutgoingContent
    {
        public TextContent(string text)
        {
            Text = text;
        }

        public string Text { get; set; }
    }

    /// <summary>
    /// A file with the metadata its message type needs. The bytes are uploaded as they are:
    /// transcoding, thumbnails and durations are the host's job, since they need codecs the
    /// protocol layer has no business carrying.
    /// </summary>
    public sealed class MediaContent : OutgoingContent
    {
        public MediaContent(byte[] content, string mediaType)
        {
            Content = content;
            MediaType = mediaType;
        }

        public byte[] Content { get; set; }

        /// <summary>One of the values in <see cref="Media.MediaType"/>.</summary>
        public string MediaType { get; set; }

        public string Mimetype { get; set; }

        public string Caption { get; set; }

        /// <summary>Small JPEG shown before the file is downloaded.</summary>
        public byte[] Thumbnail { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>Duration of audio or video.</summary>
        public int Seconds { get; set; }

        public string FileName { get; set; }

        public int PageCount { get; set; }

        /// <summary>Bar heights drawn behind a voice note, if the host computed them.</summary>
        public byte[] Waveform { get; set; }

        public bool IsAnimatedSticker { get; set; }

        /// <summary>Shows once and then becomes unreadable. Only images and videos support it.</summary>
        public bool ViewOnce { get; set; }
    }

    /// <summary>An emoji on someone's message. An empty emoji removes an earlier reaction.</summary>
    public sealed class ReactionContent : OutgoingContent
    {
        public ReactionContent(MessageEnvelopeKey key, string emoji)
        {
            Key = key;
            Emoji = emoji;
        }

        public MessageEnvelopeKey Key { get; set; }

        public string Emoji { get; set; }
    }

    /// <summary>Deletes a message for everyone.</summary>
    public sealed class DeleteContent : OutgoingContent
    {
        public DeleteContent(MessageEnvelopeKey key)
        {
            Key = key;
        }

        public MessageEnvelopeKey Key { get; set; }
    }

    /// <summary>Replaces the text of a message already sent.</summary>
    public sealed class EditContent : OutgoingContent
    {
        public EditContent(MessageEnvelopeKey key, string text)
        {
            Key = key;
            Text = text;
        }

        public MessageEnvelopeKey Key { get; set; }

        public string Text { get; set; }
    }

    /// <summary>Pins or unpins a message for everyone in the chat.</summary>
    public sealed class PinContent : OutgoingContent
    {
        /// <summary>24 hours, the shortest of the three durations the phone offers.</summary>
        public const int OneDay = 86400;

        public PinContent(MessageEnvelopeKey key, bool pin, int seconds = OneDay)
        {
            Key = key;
            Pin = pin;
            Seconds = seconds;
        }

        public MessageEnvelopeKey Key { get; set; }

        public bool Pin { get; set; }

        /// <summary>How long the pin lasts. Ignored when unpinning.</summary>
        public int Seconds { get; set; }
    }

    public sealed class LocationContent : OutgoingContent
    {
        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public string Name { get; set; }

        public string Address { get; set; }
    }

    /// <summary>One or more contacts, each already formatted as a vCard by the host.</summary>
    public sealed class ContactContent : OutgoingContent
    {
        public ContactContent()
        {
            Vcards = new List<string>();
        }

        public string DisplayName { get; set; }

        public IList<string> Vcards { get; private set; }
    }
}
