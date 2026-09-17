using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Everything a bubble needs from a message's <see cref="Proto.ContextInfo"/>: what it quotes,
    /// who it mentions, and whether it was forwarded.
    /// </summary>
    /// <remarks>
    /// Deliberately stops short of naming the quoted sender. That answer depends on the account,
    /// the alias table and the contact directory, none of which belong here — the caller resolves
    /// <see cref="QuotedParticipantJid"/> itself.
    /// </remarks>
    public sealed class QuotedContext
    {
        private static readonly QuotedContext EmptyContext = new QuotedContext();

        /// <summary>A message that quotes nothing, mentions nobody and was not forwarded.</summary>
        public static QuotedContext Empty
        {
            get { return EmptyContext; }
        }

        public bool IsForwarded { get; private set; }

        /// <summary>Normalized and de-duplicated, or null when the message mentions nobody.</summary>
        public List<string> MentionedJids { get; private set; }

        /// <summary>The id of the quoted message, when the sender included one.</summary>
        public string QuotedMessageId { get; private set; }

        /// <summary>Who wrote the quoted message, normalized. Null when the quote carries no author.</summary>
        public string QuotedParticipantJid { get; private set; }

        /// <summary>Text of the quote. Empty for media with no caption, where the strip shows the kind instead.</summary>
        public string QuotedText { get; private set; }

        public ChatPreviewKind QuotedKind { get; private set; }

        /// <summary>True when there is a quote to render, whatever it turned out to contain.</summary>
        public bool HasQuote
        {
            get { return !string.IsNullOrEmpty(QuotedParticipantJid) || !string.IsNullOrEmpty(QuotedMessageId) || !string.IsNullOrEmpty(QuotedText); }
        }

        private QuotedContext()
        {
            QuotedKind = ChatPreviewKind.Text;
        }

        public static QuotedContext Read(Proto.Message message)
        {
            var unwrapped = HistorySyncContentFilter.Unwrap(message);
            var forwarded = HistorySyncContentFilter.ReadIsForwarded(unwrapped);
            var context = HistorySyncContentFilter.GetContextInfo(unwrapped);

            if (context == null)
            {
                if (!forwarded)
                {
                    return Empty;
                }

                return new QuotedContext { IsForwarded = true };
            }

            var read = new QuotedContext
            {
                IsForwarded = forwarded,
                MentionedJids = ReadMentions(context)
            };

            if (context.QuotedMessage == null)
            {
                return read;
            }

            if (context.HasStanzaId && !string.IsNullOrWhiteSpace(context.StanzaId))
            {
                read.QuotedMessageId = context.StanzaId;
            }

            read.ReadQuotedBody(context.QuotedMessage);
            read.QuotedParticipantJid = NormalizeOrNull(context.Participant);
            return read;
        }

        private static List<string> ReadMentions(Proto.ContextInfo context)
        {
            if (context.MentionedJid == null || context.MentionedJid.Count == 0)
            {
                return null;
            }

            List<string> mentioned = null;
            for (int i = 0; i < context.MentionedJid.Count; i++)
            {
                var normalized = NormalizeOrNull(context.MentionedJid[i]);
                if (normalized == null)
                {
                    continue;
                }

                if (mentioned == null)
                {
                    mentioned = new List<string>();
                }

                if (!mentioned.Contains(normalized))
                {
                    mentioned.Add(normalized);
                }
            }

            return mentioned;
        }

        private void ReadQuotedBody(Proto.Message quoted)
        {
            var info = MessageRenderReader.Read(quoted);
            if (info == null)
            {
                return;
            }

            QuotedKind = info.PreviewKind;

            ChatPreviewKind? hint = QuotedKind == ChatPreviewKind.Text
                ? (ChatPreviewKind?)null
                : QuotedKind;

            // NormalizeBody, not Normalize: this text is persisted as HistoryMessage.QuotedBody, and
            // Normalize caps at 50 characters for a one-line strip. The strip normalizes again when
            // it draws (ChatMessageViewModel.QuotedStripText), so capping here changed nothing on
            // screen and stored a truncated quote for good — while the same quote arriving through
            // history sync was stored whole. The helper says as much on Normalize itself.
            string text;
            ChatPreviewNormalizer.NormalizeBody(info.Content ?? string.Empty, hint, out _, out text);

            if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(info.Caption))
            {
                text = info.Caption;
            }

            // Media with no caption keeps this empty on purpose: the bubble strip draws the icon and
            // a localized label from QuotedKind, rather than the "[Image]" tags older builds stored.
            QuotedText = text;
        }

        private static string NormalizeOrNull(string jid)
        {
            var normalized = JidHelper.Normalize(jid);
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }
    }
}
