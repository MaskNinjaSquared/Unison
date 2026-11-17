using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>
    /// Maps Core composer shapes to/from the client SendText envelope.
    /// </summary>
    public static class SendTextRequestMapper
    {
        public static SendTextRequest FromCore(string jid, string text)
        {
            return FromCore(jid, new OutgoingTextMentions { Text = text });
        }

        public static SendTextRequest FromCore(string jid, OutgoingTextMentions message)
        {
            if (message == null)
            {
                return new SendTextRequest { ChatJid = jid };
            }

            return new SendTextRequest
            {
                ChatJid = jid,
                Text = message.Text,
                Mentions = CopyMentions(message.Mentions),
                MentionAll = message.MentionAll,
                Quote = FromCoreQuote(message.Quote),
                MarkAsForwarded = message.MarkAsForwarded,
                ForwardingScore = message.ForwardingScore
            };
        }

        public static OutgoingTextMentions ToCoreMentions(SendTextRequest request)
        {
            if (request == null)
            {
                return null;
            }

            return new OutgoingTextMentions
            {
                Text = request.Text,
                Mentions = CopyMentions(request.Mentions),
                MentionAll = request.MentionAll,
                Quote = ToCoreQuote(request.Quote),
                MarkAsForwarded = request.MarkAsForwarded,
                ForwardingScore = request.ForwardingScore
            };
        }

        public static OutgoingQuoteRequest FromCoreQuote(OutgoingQuote quote)
        {
            if (quote == null)
            {
                return null;
            }

            return new OutgoingQuoteRequest
            {
                MessageId = quote.MessageId,
                ChatJid = quote.ChatJid,
                ParticipantJid = quote.ParticipantJid,
                AuthorName = quote.AuthorName,
                FromMe = quote.FromMe,
                Kind = quote.Kind.ToString(),
                Text = quote.Text
            };
        }

        public static OutgoingQuote ToCoreQuote(OutgoingQuoteRequest quote)
        {
            if (quote == null)
            {
                return null;
            }

            return new OutgoingQuote
            {
                MessageId = quote.MessageId,
                ChatJid = quote.ChatJid,
                ParticipantJid = quote.ParticipantJid,
                AuthorName = quote.AuthorName,
                FromMe = quote.FromMe,
                Kind = ParsePreviewKind(quote.Kind),
                Text = quote.Text
            };
        }

        private static List<string> CopyMentions(IList<string> mentions)
        {
            if (mentions == null || mentions.Count == 0)
            {
                return null;
            }

            return new List<string>(mentions);
        }

        private static ChatPreviewKind ParsePreviewKind(string kind)
        {
            ChatPreviewKind parsed;
            if (!string.IsNullOrWhiteSpace(kind) &&
                Enum.TryParse(kind, ignoreCase: true, out parsed))
            {
                return parsed;
            }

            return ChatPreviewKind.Text;
        }
    }
}
